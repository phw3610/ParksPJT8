using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace ScreenRecorder
{
    // Recording preferences, kept between runs in %AppData%\ScreenRecorder\settings.cfg.
    internal sealed class AppSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRecorder", "settings.cfg");

        public const int MinBitrateKbps = 300;
        public const int MaxBitrateKbps = 20000;
        public const int MinFps = 1;
        public const int MaxFps = 30;
        public const int MinPartMegabytes = 50;
        public const int MaxPartMegabytes = 10000;
        public const int MinScaleWidth = 320;
        public const int MinPort = 1024;
        public const int MaxPort = 65535;

        public int Monitor = 0;             // index into Screen.AllScreens
        public int ScaleWidth = 0;          // 0 = the screen's own width
        public int BitrateKbps = 3000;
        public int Fps = 10;                // lower fps is the cheapest way to shrink a screen recording
        public int PartMegabytes = 2000;    // start a new file after this much
        public bool HardwareEncoder = true;  // GPU H.264 encoder first; off = software encoder only (for black/broken videos)
        public int CaptureMode = 0;          // 0 = automatic (DXGI, then GDI), 1 = Windows Graphics Capture, 2 = GDI only
        public bool Compress = false;       // re-encode with ffmpeg after recording (only if ffmpeg.exe is next to the program)
        public bool KeepOriginal = false;   // with Compress: save the small copy as *-small.mp4 and keep the original
        public string OutputFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder");

        public uint HotKeyModifiers = HotKey.Control | HotKey.Shift;
        public int HotKeyCode = (int)Keys.F9;

        public bool ApiEnabled = false;
        public int ApiPort = 47800;
        public string ApiToken = string.Empty;

        public string ScheduleText = string.Empty;   // see Schedule.Serialize; empty = no schedule

        public long PartBytes { get { return (long)PartMegabytes * 1024 * 1024; } }

        public AppSettings Clone()
        {
            return (AppSettings)MemberwiseClone();
        }

        public void Clamp()
        {
            if (Monitor < 0) Monitor = 0;
            if (ScaleWidth < 0) ScaleWidth = 0;
            if (ScaleWidth > 0 && ScaleWidth < MinScaleWidth) ScaleWidth = MinScaleWidth;
            BitrateKbps = Math.Max(MinBitrateKbps, Math.Min(MaxBitrateKbps, BitrateKbps));
            Fps = Math.Max(MinFps, Math.Min(MaxFps, Fps));
            PartMegabytes = Math.Max(MinPartMegabytes, Math.Min(MaxPartMegabytes, PartMegabytes));
            ApiPort = Math.Max(MinPort, Math.Min(MaxPort, ApiPort));
            CaptureMode = Math.Max(0, Math.Min(2, CaptureMode));
            if (string.IsNullOrWhiteSpace(OutputFolder))
            {
                OutputFolder = new AppSettings().OutputFolder;
            }
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    Dictionary<string, string> values = new Dictionary<string, string>();
                    foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int split = line.IndexOf('=');
                        if (split > 0) values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                    }

                    s.Monitor = Int(values, "Monitor", s.Monitor);
                    s.ScaleWidth = Int(values, "ScaleWidth", s.ScaleWidth);
                    s.BitrateKbps = Int(values, "BitrateKbps", s.BitrateKbps);
                    s.Fps = Int(values, "Fps", s.Fps);
                    s.PartMegabytes = Int(values, "PartMegabytes", s.PartMegabytes);
                    s.HardwareEncoder = Int(values, "HardwareEncoder", s.HardwareEncoder ? 1 : 0) != 0;
                    s.CaptureMode = Int(values, "CaptureMode", s.CaptureMode);
                    s.Compress = Int(values, "Compress", s.Compress ? 1 : 0) != 0;
                    s.KeepOriginal = Int(values, "KeepOriginal", s.KeepOriginal ? 1 : 0) != 0;
                    s.HotKeyModifiers = (uint)Int(values, "HotKeyModifiers", (int)s.HotKeyModifiers);
                    s.HotKeyCode = Int(values, "HotKeyCode", s.HotKeyCode);
                    s.ApiEnabled = Int(values, "ApiEnabled", s.ApiEnabled ? 1 : 0) != 0;
                    s.ApiPort = Int(values, "ApiPort", s.ApiPort);
                    string text;
                    if (values.TryGetValue("OutputFolder", out text)) s.OutputFolder = text;
                    if (values.TryGetValue("ApiToken", out text)) s.ApiToken = text;
                    if (values.TryGetValue("ScheduleText", out text)) s.ScheduleText = text;
                }
            }
            catch
            {
                // an unreadable file just means the defaults apply
            }

            if (!HotKey.IsValid(s.HotKeyModifiers, s.HotKeyCode))
            {
                s.HotKeyModifiers = 0;
                s.HotKeyCode = 0;
            }

            // The token is what keeps a web page from starting a recording through
            // the local API, so it is generated once and never left empty.
            if (string.IsNullOrEmpty(s.ApiToken))
            {
                s.ApiToken = NewToken();
                s.Save();
            }

            s.Clamp();
            return s;
        }

        // Returns null on success, or a message describing why nothing was saved.
        public string Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                StringBuilder text = new StringBuilder();
                text.AppendLine("Monitor=" + Monitor.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ScaleWidth=" + ScaleWidth.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("BitrateKbps=" + BitrateKbps.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("Fps=" + Fps.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("PartMegabytes=" + PartMegabytes.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("HardwareEncoder=" + (HardwareEncoder ? "1" : "0"));
                text.AppendLine("CaptureMode=" + CaptureMode.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("Compress=" + (Compress ? "1" : "0"));
                text.AppendLine("KeepOriginal=" + (KeepOriginal ? "1" : "0"));
                text.AppendLine("OutputFolder=" + OutputFolder);
                text.AppendLine("HotKeyModifiers=" + HotKeyModifiers.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("HotKeyCode=" + HotKeyCode.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ApiEnabled=" + (ApiEnabled ? "1" : "0"));
                text.AppendLine("ApiPort=" + ApiPort.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ApiToken=" + ApiToken);
                text.AppendLine("ScheduleText=" + ScheduleText);
                File.WriteAllText(FilePath, text.ToString(), Encoding.UTF8);
                return null;
            }
            catch (Exception ex)
            {
                return "설정을 저장하지 못했습니다: " + ex.Message;
            }
        }

        private static string NewToken()
        {
            byte[] bytes = new byte[16];
            using (RNGCryptoServiceProvider random = new RNGCryptoServiceProvider())
            {
                random.GetBytes(bytes);
            }

            StringBuilder text = new StringBuilder();
            foreach (byte b in bytes) text.Append(b.ToString("x2"));
            return text.ToString();
        }

        private static int Int(Dictionary<string, string> values, string name, int fallback)
        {
            string text;
            int value;
            if (values.TryGetValue(name, out text) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return fallback;
        }
    }
}
