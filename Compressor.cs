using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ScreenRecorder
{
    // Optional second pass: when ffmpeg.exe sits next to this program, a finished
    // recording can be re-encoded with x264 (quality-based, CRF), which usually makes
    // a screen recording several times smaller. The original is replaced only after
    // ffmpeg succeeded and the result is really smaller, so a failure never loses a recording.
    internal sealed class Compressor : IDisposable
    {
        private const string Preset = "slow";
        private const int Crf = 28;

        private readonly object _sync = new object();
        private readonly Queue<KeyValuePair<string, bool>> _queue = new Queue<KeyValuePair<string, bool>>();   // file, keep the original
        private Thread _worker;
        private Process _running;
        private string _current = string.Empty;
        private bool _cancelled;

        // Raised from the worker thread: one line per finished or failed file.
        public event Action<string> Message;

        public static string FfmpegPath
        {
            get
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
                return File.Exists(path) ? path : null;
            }
        }

        public static bool Available { get { return FfmpegPath != null; } }

        // File being compressed right now (empty when idle) and how many are waiting.
        public string Current { get { lock (_sync) return _current; } }
        public int Pending { get { lock (_sync) return _queue.Count; } }

        public void Enqueue(IEnumerable<string> files, bool keepOriginal)
        {
            lock (_sync)
            {
                _cancelled = false;
                foreach (string file in files) _queue.Enqueue(new KeyValuePair<string, bool>(file, keepOriginal));
                if (_worker != null && _worker.IsAlive) return;
                _worker = new Thread(Work) { Name = "Compressor", IsBackground = true };
                _worker.Start();
            }
        }

        // For shutdown: stop ffmpeg and drop the queue. Originals stay as they are.
        public void Cancel()
        {
            Process running;
            lock (_sync)
            {
                _cancelled = true;
                _queue.Clear();
                running = _running;
            }

            try { if (running != null && !running.HasExited) running.Kill(); }
            catch { }
        }

        public void Dispose()
        {
            Cancel();
        }

        private void Work()
        {
            while (true)
            {
                string file;
                bool keep;
                lock (_sync)
                {
                    if (_queue.Count == 0) { _current = string.Empty; return; }
                    KeyValuePair<string, bool> job = _queue.Dequeue();
                    file = job.Key;
                    keep = job.Value;
                    _current = Path.GetFileName(file);
                }

                try { Compress(file, keep); }
                catch (Exception ex) { Raise("압축하지 못했습니다 (" + Path.GetFileName(file) + "): " + ex.Message); }
            }
        }

        private void Compress(string file, bool keepOriginal)
        {
            string ffmpeg = FfmpegPath;
            if (ffmpeg == null || !File.Exists(file)) return;

            string temp = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + ".compressing.mp4");
            long before = new FileInfo(file).Length;
            StringBuilder errors = new StringBuilder();

            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -loglevel error -y -i \"" + file + "\" -c:v libx264 -preset " + Preset +
                            " -crf " + Crf + " -pix_fmt yuv420p -movflags +faststart \"" + temp + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            int exitCode;
            using (Process process = new Process { StartInfo = info })
            {
                process.ErrorDataReceived += (s, e) => { if (e.Data != null && errors.Length < 2000) errors.AppendLine(e.Data); };
                process.Start();
                lock (_sync) _running = process;
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }   // keep the PC (and a new recording) responsive
                catch { }
                process.BeginErrorReadLine();
                process.WaitForExit();
                exitCode = process.ExitCode;
                lock (_sync) _running = null;
            }

            bool cancelled;
            lock (_sync) cancelled = _cancelled;

            try
            {
                long after = File.Exists(temp) ? new FileInfo(temp).Length : 0;
                if (cancelled)
                {
                    return;
                }

                if (exitCode != 0 || after == 0)
                {
                    Raise("압축하지 못해 원본을 유지합니다 (" + Path.GetFileName(file) + "): " + LastLine(errors.ToString()));
                }
                else if (after >= before)
                {
                    Raise("압축해도 작아지지 않아 압축본을 만들지 않았습니다: " + Path.GetFileName(file));
                }
                else
                {
                    if (keepOriginal)
                    {
                        string small = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + "-small.mp4");
                        if (File.Exists(small)) File.Delete(small);
                        File.Move(temp, small);
                        Raise("압축본 저장: " + Path.GetFileName(small) + " (" + Megabytes(before) + " → " + Megabytes(after) + ", 원본 유지)");
                        return;
                    }

                    File.Replace(temp, file, null);
                    Raise("압축 완료: " + Path.GetFileName(file) + " (" + Megabytes(before) + " → " + Megabytes(after) + ")");
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }

        // ffmpeg repeats itself on failure; the last line is the useful one.
        private static string LastLine(string text)
        {
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "알 수 없는 오류" : lines[lines.Length - 1].Trim();
        }

        private static string Megabytes(long bytes)
        {
            return (bytes / (1024.0 * 1024)).ToString("0.0") + "MB";
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
