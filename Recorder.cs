using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ScreenRecorder
{
    // Records one monitor to MP4 on a background thread. Start/Stop can be called
    // from the window, the hotkey or the API; Stop only asks, and IsActive stays
    // true until the file is finalized.
    internal sealed class Recorder
    {
        private const long MinFreeBytes = 512L * 1024 * 1024;

        private readonly object _sync = new object();
        private Thread _thread;
        private volatile bool _stopRequested;
        private volatile bool _active;
        private volatile string _currentFile = string.Empty;
        private long _bytes;
        private long _frames;
        private volatile string _backend = string.Empty;
        private DateTime _startedAt;

        // Raised from the recorder thread on start, stop, file change and failure.
        public event Action<string> Message;

        // Raised from the recorder thread once every file of a recording is finalized.
        public event Action<AppSettings, string[]> Finished;

        public bool IsActive { get { return _active; } }
        public bool IsStopping { get { return _active && _stopRequested; } }
        public string CurrentFile { get { return _currentFile; } }
        public long Bytes { get { return Interlocked.Read(ref _bytes); } }
        public long Frames { get { return Interlocked.Read(ref _frames); } }

        // "DXGI" (fast, GPU) or "GDI" (slow fallback) for the frames being recorded.
        public string Backend { get { return _active ? _backend : string.Empty; } }
        public TimeSpan Elapsed { get { return _active ? DateTime.Now - _startedAt : TimeSpan.Zero; } }

        public static Size OutputSize(Size screen, int scaleWidth)
        {
            int width = screen.Width;
            int height = screen.Height;
            if (scaleWidth > 0 && scaleWidth < width)
            {
                height = (int)Math.Round(height * (scaleWidth / (double)width));
                width = scaleWidth;
            }

            // H.264 needs even dimensions.
            return new Size(Math.Max(2, width & ~1), Math.Max(2, height & ~1));
        }

        // Returns null on success, or a message describing why recording did not start.
        public string Start(AppSettings settings)
        {
            lock (_sync)
            {
                if (_active) return _stopRequested ? "이전 녹화를 저장하는 중입니다." : "이미 녹화 중입니다.";

                AppSettings s = settings.Clone();
                s.Clamp();

                Rectangle bounds = ScreenBounds(s.Monitor);
                Size size = OutputSize(bounds.Size, s.ScaleWidth);

                try { Directory.CreateDirectory(s.OutputFolder); }
                catch (Exception ex) { return "저장 폴더를 만들 수 없습니다: " + ex.Message; }

                if (FreeBytes(s.OutputFolder) < MinFreeBytes) return "디스크 여유 공간이 부족합니다.";

                DateTime now = DateTime.Now;
                string baseName = "ScreenRecord-" + now.ToString("yyyyMMdd-HHmmss");

                _stopRequested = false;
                Interlocked.Exchange(ref _bytes, 0);
                Interlocked.Exchange(ref _frames, 0);
                _backend = string.Empty;
                _startedAt = now;
                _currentFile = PathFor(s.OutputFolder, baseName, 1);
                _active = true;

                // Media Foundation objects belong to the thread that made them, and the
                // caller may be the STA UI thread, so every writer is created, used and
                // finalized on the recorder thread. Wait here only to report a start failure.
                string startError = null;
                using (ManualResetEventSlim started = new ManualResetEventSlim(false))
                {
                    _thread = new Thread(() => Run(s, bounds, size, baseName, started, e => startError = e));
                    _thread.Name = "ScreenRecorder";
                    _thread.Start();
                    if (!started.Wait(15000)) startError = "녹화 시작 시간이 초과되었습니다.";
                }

                if (startError != null) _active = false;
                return startError;
            }
        }

        // Returns null on success, or a message describing why nothing was stopped.
        public string Stop()
        {
            lock (_sync)
            {
                if (!_active || _stopRequested) return "녹화 중이 아닙니다.";
                _stopRequested = true;
                return null;
            }
        }

        // For shutdown: give the thread time to finalize the file.
        public void WaitForStop(int milliseconds)
        {
            Thread thread = _thread;
            if (thread != null && thread.IsAlive) thread.Join(milliseconds);
        }

        private void Run(AppSettings s, Rectangle bounds, Size size, string baseName,
                         ManualResetEventSlim started, Action<string> reportStartError)
        {
            string error;
            Mp4Writer writer = Mp4Writer.TryCreate(PathFor(s.OutputFolder, baseName, 1),
                size.Width, size.Height, s.Fps, s.BitrateKbps * 1000, s.HardwareEncoder, out error);
            if (writer == null)
            {
                reportStartError("MP4 인코더를 시작할 수 없습니다: " + error);
                started.Set();
                return;
            }

            started.Set();

            int part = 1;
            Bitmap frame = null;
            ScreenGrabber grabber = null;
            string lastFile = writer.Path;
            try
            {
                frame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
                grabber = new ScreenGrabber(GetScreen(s.Monitor), size, s.CaptureMode);
                Raise("녹화 시작: " + Path.GetFileName(writer.Path));

                Stopwatch clock = Stopwatch.StartNew();
                int delay = Math.Max(1, 1000 / s.Fps);
                long due = 0;
                long ticks = 0;
                int failures = 0;

                while (!_stopRequested)
                {
                    due += delay;
                    int wait = (int)(due - clock.ElapsedMilliseconds);
                    if (wait > 0) Thread.Sleep(wait);
                    else if (wait < -2000) due = clock.ElapsedMilliseconds;   // fell far behind; resync

                    ticks++;

                    // The encoder is fixed to one size, so a resolution change
                    // (display settings, monitor unplugged) continues in a new file.
                    if (ticks % s.Fps == 0)
                    {
                        Rectangle current = ScreenBounds(s.Monitor);
                        if (current.Size != bounds.Size)
                        {
                            bounds = current;
                            size = OutputSize(bounds.Size, s.ScaleWidth);
                            writer = Roll(writer, s, baseName, ++part, size, "해상도가 바뀌어 새 파일로 이어서 녹화합니다: ");
                            if (writer == null) break;
                            frame.Dispose();
                            frame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
                            grabber.Dispose();
                            grabber = new ScreenGrabber(GetScreen(s.Monitor), size, s.CaptureMode);
                        }
                    }

                    // On failure keep the previous pixels so the video still holds real-time
                    // timing, but say so: a recording of stale frames should not look fine.
                    try
                    {
                        grabber.Grab(frame);
                        failures = 0;
                    }
                    catch (Exception ex)
                    {
                        if (++failures == 15) Raise("화면 캡처가 계속 실패하고 있습니다: " + ex.Message);
                    }

                    _backend = grabber.Backend;
                    writer.AddFrame(frame);
                    Interlocked.Increment(ref _frames);
                    lastFile = writer.Path;

                    if (ticks % s.Fps == 0)
                    {
                        long length = writer.Length;
                        Interlocked.Exchange(ref _bytes, length);

                        if (length >= s.PartBytes)
                        {
                            writer = Roll(writer, s, baseName, ++part, size, "파일 크기 한도에 도달해 새 파일로 이어서 녹화합니다: ");
                            if (writer == null) break;
                        }
                        else if (FreeBytes(s.OutputFolder) < MinFreeBytes)
                        {
                            Raise("디스크 여유 공간이 부족해 녹화를 중지합니다.");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Raise("녹화 오류: " + ex.Message);
            }
            finally
            {
                if (writer != null)
                {
                    try { writer.Dispose(); }
                    catch { }
                    try { Interlocked.Exchange(ref _bytes, new FileInfo(writer.Path).Length); }
                    catch { }
                }
                if (grabber != null) grabber.Dispose();
                if (frame != null) frame.Dispose();

                int seconds = (int)(DateTime.Now - _startedAt).TotalSeconds;
                long megabytes = Bytes / (1024 * 1024);
                // Cleared first so the window already shows "idle" when the message arrives.
                _active = false;
                Raise("녹화 종료: " + Path.GetFileName(lastFile) + " (" + seconds + "초, " + megabytes + "MB)");

                List<string> files = new List<string>();
                for (int i = 1; i <= part; i++)
                {
                    string path = PathFor(s.OutputFolder, baseName, i);
                    if (File.Exists(path)) files.Add(path);
                }

                Action<AppSettings, string[]> finished = Finished;
                if (finished != null)
                {
                    try { finished(s, files.ToArray()); }
                    catch { }
                }
            }
        }

        // Returns null when the next file could not be opened, which ends the recording.
        private Mp4Writer Roll(Mp4Writer writer, AppSettings s, string baseName, int part, Size size, string message)
        {
            writer.Dispose();
            string error;
            Mp4Writer next = Mp4Writer.TryCreate(PathFor(s.OutputFolder, baseName, part),
                size.Width, size.Height, s.Fps, s.BitrateKbps * 1000, s.HardwareEncoder, out error);
            if (next == null)
            {
                Raise("다음 녹화 파일을 열지 못해 녹화를 중지합니다: " + error);
                return null;
            }

            _currentFile = next.Path;
            Raise(message + Path.GetFileName(next.Path));
            return next;
        }

        public static Screen GetScreen(int monitor)
        {
            Screen[] screens = Screen.AllScreens;
            return screens[Math.Max(0, Math.Min(monitor, screens.Length - 1))];
        }

        public static Rectangle ScreenBounds(int monitor)
        {
            return GetScreen(monitor).Bounds;
        }

        private static string PathFor(string folder, string baseName, int part)
        {
            return Path.Combine(folder, (part <= 1 ? baseName : baseName + "-" + part) + ".mp4");
        }

        private static long FreeBytes(string folder)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))).AvailableFreeSpace; }
            catch { return long.MaxValue; }
        }

        private void Raise(string message)
        {
            Action<string> handler = Message;
            if (handler == null) return;
            try { handler(message); }
            catch { }
        }
    }
}
