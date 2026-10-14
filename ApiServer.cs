using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;

namespace ScreenRecorder
{
    // Local control API: /start /stop /toggle /status /folder.
    //
    // Loopback only, and every request needs the token. Without the token any web
    // page open in the browser could fire a request at 127.0.0.1 and start a
    // recording, so a request without it is refused, and so is a Host header that
    // is not a loopback name (DNS rebinding).
    internal sealed class ApiServer : IDisposable
    {
        private readonly Recorder _recorder;
        private readonly Func<AppSettings> _currentSettings;
        private HttpListener _listener;
        private Thread _thread;
        private string _token = string.Empty;

        public ApiServer(Recorder recorder, Func<AppSettings> currentSettings)
        {
            _recorder = recorder;
            _currentSettings = currentSettings;
        }

        // Returns null on success, or a message describing why the API is not listening.
        public string Start(int port, string token)
        {
            Stop();
            try
            {
                _token = token;
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                _listener.Start();
            }
            catch (Exception ex)
            {
                _listener = null;
                return "API를 시작할 수 없습니다 (포트 " + port + "): " + ex.Message;
            }

            HttpListener listener = _listener;
            _thread = new Thread(() => Loop(listener));
            _thread.IsBackground = true;
            _thread.Name = "ScreenRecorder API";
            _thread.Start();
            return null;
        }

        public void Stop()
        {
            HttpListener listener = _listener;
            _listener = null;
            if (listener == null) return;
            try { listener.Close(); }
            catch { }
        }

        public void Dispose()
        {
            Stop();
        }

        private void Loop(HttpListener listener)
        {
            while (true)
            {
                HttpListenerContext context;
                try { context = listener.GetContext(); }
                catch { return; }   // listener closed

                try { Handle(context); }
                catch (Exception ex) { Reply(context, 500, false, ex.Message, null); }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;

            string host = request.Url.Host;
            if (host != "127.0.0.1" && host != "localhost")
            {
                Reply(context, 403, false, "forbidden host", null);
                return;
            }

            if (!TokenMatches(request))
            {
                Reply(context, 401, false, "invalid or missing token", null);
                return;
            }

            string path = request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
            if (path == "/status")
            {
                Reply(context, 200, true, null, Status());
            }
            else if (path == "/start")
            {
                string error = _recorder.Start(WithOverrides(_currentSettings(), request.QueryString));
                Reply(context, error == null ? 200 : 409, error == null, error ?? "recording started", Status());
            }
            else if (path == "/stop")
            {
                string error = _recorder.Stop();
                Reply(context, error == null ? 200 : 409, error == null, error ?? "stopping", Status());
            }
            else if (path == "/toggle")
            {
                string error = _recorder.IsActive
                    ? _recorder.Stop()
                    : _recorder.Start(WithOverrides(_currentSettings(), request.QueryString));
                Reply(context, error == null ? 200 : 409, error == null, error ?? "ok", Status());
            }
            else if (path == "/folder")
            {
                string folder = _currentSettings().OutputFolder;
                try
                {
                    System.IO.Directory.CreateDirectory(folder);
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + folder + "\"", UseShellExecute = true });
                    Reply(context, 200, true, folder, Status());
                }
                catch (Exception ex)
                {
                    Reply(context, 500, false, ex.Message, Status());
                }
            }
            else
            {
                Reply(context, 404, false, "unknown endpoint; use /start /stop /toggle /status /folder", null);
            }
        }

        // A caller can override the saved settings for a single recording.
        //   screen, scale, kbps, fps, mb
        private static AppSettings WithOverrides(AppSettings settings, NameValueCollection query)
        {
            AppSettings s = settings.Clone();
            s.Monitor = ParseInt(query["screen"], s.Monitor);
            s.ScaleWidth = ParseInt(query["scale"], s.ScaleWidth);
            s.BitrateKbps = ParseInt(query["kbps"], s.BitrateKbps);
            s.Fps = ParseInt(query["fps"], s.Fps);
            s.PartMegabytes = ParseInt(query["mb"], s.PartMegabytes);
            return s;
        }

        private bool TokenMatches(HttpListenerRequest request)
        {
            string given = request.QueryString["token"];
            if (string.IsNullOrEmpty(given)) given = request.Headers["X-Token"];
            if (string.IsNullOrEmpty(given))
            {
                string auth = request.Headers["Authorization"];
                if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) given = auth.Substring(7).Trim();
            }

            if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(_token) || given.Length != _token.Length) return false;

            // constant time, so the token cannot be guessed one character at a time
            int difference = 0;
            for (int i = 0; i < given.Length; i++) difference |= given[i] ^ _token[i];
            return difference == 0;
        }

        private string Status()
        {
            bool recording = _recorder.IsActive;
            return "{\"recording\":" + (recording ? "true" : "false")
                 + ",\"stopping\":" + (_recorder.IsStopping ? "true" : "false")
                 + ",\"file\":" + Quote(recording ? _recorder.CurrentFile : string.Empty)
                 + ",\"seconds\":" + (int)_recorder.Elapsed.TotalSeconds
                 + ",\"frames\":" + _recorder.Frames
                 + ",\"capture\":" + Quote(_recorder.Backend)
                 + ",\"bytes\":" + _recorder.Bytes + "}";
        }

        private static void Reply(HttpListenerContext context, int code, bool ok, string message, string status)
        {
            StringBuilder body = new StringBuilder("{\"ok\":").Append(ok ? "true" : "false");
            if (message != null) body.Append(",\"message\":").Append(Quote(message));
            if (status != null) body.Append(",\"status\":").Append(status);
            body.Append('}');

            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString());
            try
            {
                HttpListenerResponse response = context.Response;
                response.StatusCode = code;
                response.ContentType = "application/json; charset=utf-8";
                response.Headers["Cache-Control"] = "no-store";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.Close();
            }
            catch
            {
                // the caller went away
            }
        }

        private static string Quote(string text)
        {
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in text ?? string.Empty)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        private static int ParseInt(string text, int fallback)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }
    }
}
