using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenRecorder
{
    internal static class HotKey
    {
        public const uint Alt = 0x0001;
        public const uint Control = 0x0002;
        public const uint Shift = 0x0004;
        // Without this, holding the keys down would toggle recording over and over.
        public const uint NoRepeat = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // A bare key would be swallowed everywhere in Windows, so a modifier is required.
        public static bool IsValid(uint modifiers, int keyCode)
        {
            if ((modifiers & (Alt | Control | Shift)) == 0) return false;

            Keys key = (Keys)keyCode;
            return key == Keys.Space
                || (key >= Keys.D0 && key <= Keys.D9)
                || (key >= Keys.A && key <= Keys.Z)
                || (key >= Keys.F1 && key <= Keys.F12);
        }

        public static string Format(uint modifiers, int keyCode)
        {
            if (!IsValid(modifiers, keyCode)) return string.Empty;

            string text = string.Empty;
            if ((modifiers & Control) != 0) text += "Ctrl+";
            if ((modifiers & Alt) != 0) text += "Alt+";
            if ((modifiers & Shift) != 0) text += "Shift+";

            Keys key = (Keys)keyCode;
            return text + (key >= Keys.D0 && key <= Keys.D9 ? ((int)(key - Keys.D0)).ToString() : key.ToString());
        }
    }

    // Read-only box that shows the combination the user presses.
    internal sealed class HotKeyBox : TextBox
    {
        private uint _modifiers;
        private int _keyCode;

        public event EventHandler Changed;

        public HotKeyBox()
        {
            ReadOnly = true;
            BackColor = System.Drawing.SystemColors.Window;
        }

        public uint Modifiers { get { return _modifiers; } }
        public int KeyCode { get { return _keyCode; } }

        public void SetValue(uint modifiers, int keyCode)
        {
            bool valid = HotKey.IsValid(modifiers, keyCode);
            _modifiers = valid ? modifiers : 0;
            _keyCode = valid ? keyCode : 0;
            Text = valid ? HotKey.Format(_modifiers, _keyCode) : "(없음)";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Tab || keyData == (Keys.Shift | Keys.Tab))
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }

            if (keyData == Keys.Delete || keyData == Keys.Back)
            {
                Apply(0, 0);
                return true;
            }

            uint modifiers = 0;
            if ((keyData & Keys.Control) != 0) modifiers |= HotKey.Control;
            if ((keyData & Keys.Alt) != 0) modifiers |= HotKey.Alt;
            if ((keyData & Keys.Shift) != 0) modifiers |= HotKey.Shift;

            int key = (int)(keyData & Keys.KeyCode);
            if (HotKey.IsValid(modifiers, key)) Apply(modifiers, key);
            return true;   // keep every other key out of the box
        }

        private void Apply(uint modifiers, int keyCode)
        {
            SetValue(modifiers, keyCode);
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
