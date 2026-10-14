using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ScreenRecorder
{
    // Fetches ffmpeg.exe next to this program, for the optional extra compression.
    // gyan.dev is the Windows build host that ffmpeg.org links to; its "release essentials"
    // zip always holds the latest release, together with a .sha256 file that is checked here.
    internal static class FfmpegDownloader
    {
        public const string ZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private const string HashUrl = ZipUrl + ".sha256";

        public static string Destination
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"); }
        }

        // Runs on a worker thread. Returns null on success, otherwise a message for the user.
        public static string Download(IProgress<int> progress, CancellationToken cancel)
        {
            string destination = Destination;
            string part = destination + ".part";
            string zip = Path.Combine(Path.GetTempPath(), "ScreenRecorder-ffmpeg-" + Guid.NewGuid().ToString("N") + ".zip");

            try
            {
                // Fail early, before 115 MB are downloaded, if the program folder is read-only.
                try { File.WriteAllBytes(part, new byte[0]); }
                catch (Exception ex)
                {
                    return "프로그램 폴더에 저장할 수 없습니다 (" + Path.GetDirectoryName(destination) + "): " + ex.Message;
                }

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                string expected = ReadExpectedHash();
                if (expected == null) return "ffmpeg 체크섬을 가져오지 못했습니다. 인터넷 연결을 확인하세요.";

                string actual = DownloadFile(zip, progress, cancel);
                if (cancel.IsCancellationRequested) return "ffmpeg 받기를 취소했습니다.";
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return "내려받은 파일의 체크섬이 맞지 않아 사용하지 않습니다.";
                }

                progress.Report(100);
                using (ZipArchive archive = ZipFile.OpenRead(zip))
                {
                    ZipArchiveEntry entry = null;
                    foreach (ZipArchiveEntry candidate in archive.Entries)
                    {
                        if (candidate.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) { entry = candidate; break; }
                    }

                    if (entry == null) return "내려받은 파일에서 ffmpeg.exe를 찾지 못했습니다.";
                    using (Stream from = entry.Open())
                    using (FileStream to = new FileStream(part, FileMode.Create, FileAccess.Write))
                    {
                        from.CopyTo(to);
                    }
                }

                if (File.Exists(destination)) File.Delete(destination);
                File.Move(part, destination);

                if (!Runs(destination))
                {
                    try { File.Delete(destination); }
                    catch { }
                    return "내려받은 ffmpeg가 실행되지 않습니다.";
                }

                return null;
            }
            catch (Exception ex)
            {
                return cancel.IsCancellationRequested ? "ffmpeg 받기를 취소했습니다." : "ffmpeg를 받지 못했습니다: " + ex.Message;
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); }
                catch { }
                try { if (File.Exists(part)) File.Delete(part); }
                catch { }
            }
        }

        private static string ReadExpectedHash()
        {
            HttpWebRequest request = NewRequest(HashUrl);
            using (WebResponse response = request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.ASCII))
            {
                string text = reader.ReadToEnd().Trim();
                return text.Length >= 64 && IsHex(text.Substring(0, 64)) ? text.Substring(0, 64) : null;
            }
        }

        // Saves the response to `path` and returns its SHA-256 (hex), reporting percent done.
        private static string DownloadFile(string path, IProgress<int> progress, CancellationToken cancel)
        {
            HttpWebRequest request = NewRequest(ZipUrl);
            using (WebResponse response = request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (SHA256 sha = SHA256.Create())
            {
                long total = response.ContentLength;
                long done = 0;
                int last = -1;
                byte[] buffer = new byte[81920];
                int read;
                while (!cancel.IsCancellationRequested && (read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    int percent = total > 0 ? (int)Math.Min(99, done * 100 / total) : 0;
                    if (percent != last) { last = percent; progress.Report(percent); }
                }

                if (cancel.IsCancellationRequested) return null;
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty);
            }
        }

        private static HttpWebRequest NewRequest(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "ScreenRecorder";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            return request;
        }

        private static bool IsHex(string text)
        {
            foreach (char c in text)
            {
                if (!Uri.IsHexDigit(c)) return false;
            }

            return true;
        }

        private static bool Runs(string exe)
        {
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe, Arguments = "-version", UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true
                }))
                {
                    process.StandardOutput.ReadToEnd();
                    return process.WaitForExit(15000) && process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
