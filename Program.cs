using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenRecorder
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);   // -1 = any process


        // --tray starts hidden in the notification area.
        [STAThread]
        private static void Main(string[] args)
        {
            bool first;
            using (Mutex instance = new Mutex(true, @"Local\ScreenRecorder.Instance", out first))
            using (EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ScreenRecorder.Show"))
            {
                if (!first)
                {
                    // This process was just launched by the user, so it may hand the
                    // foreground right to the running instance; without it Windows only
                    // flashes the taskbar button instead of raising the window.
                    AllowSetForegroundWindow(-1);
                    showSignal.Set();
                    return;
                }

                EmbeddedAssemblyResolver.Register();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(args.Contains("--tray"), showSignal));
            }
        }
    }
}
