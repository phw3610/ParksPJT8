using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenRecorder
{
    // Copies one monitor into a bitmap, mouse cursor included, shrinking it when
    // the output is smaller than the screen. DXGI Desktop Duplication is used when
    // it works; GDI is the (much slower) fallback, e.g. on the secure desktop.
    internal sealed class ScreenGrabber : IDisposable
    {
        private readonly Screen _screen;
        private readonly Rectangle _bounds;
        private readonly Bitmap _native;   // full-size staging copy, only when the output is a different size
        private readonly AreaScaler _scaler;
        private DxgiCapture _dxgi;
        private DateTime _dxgiRetryAt = DateTime.MinValue;

        // "DXGI" or "GDI": which path produced the last frame.
        public string Backend { get; private set; }

        public ScreenGrabber(Screen screen, Size output)
        {
            _screen = screen;
            _bounds = screen.Bounds;
            Backend = "DXGI";
            if (output != _bounds.Size)
            {
                _native = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppRgb);
                _scaler = new AreaScaler(_bounds.Width, _bounds.Height, output.Width, output.Height);
            }
        }

        public void Grab(Bitmap target)
        {
            Bitmap source = _native ?? target;
            bool captured = TryGrabDxgi(source);
            Backend = captured ? "DXGI" : "GDI";

            using (Graphics graphics = Graphics.FromImage(source))
            {
                if (!captured) CopyScreen(graphics);
                DrawCursor(graphics);
            }

            if (_native == null) return;
            Rectangle nativeArea = new Rectangle(0, 0, _native.Width, _native.Height);
            Rectangle targetArea = new Rectangle(0, 0, target.Width, target.Height);
            BitmapData from = _native.LockBits(nativeArea, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            BitmapData to = target.LockBits(targetArea, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                _scaler.Scale(from.Scan0, from.Stride, to.Scan0, to.Stride);
            }
            finally
            {
                target.UnlockBits(to);
                _native.UnlockBits(from);
            }
        }

        public void Dispose()
        {
            if (_dxgi != null) _dxgi.Dispose();
            if (_native != null) _native.Dispose();
        }

        private bool TryGrabDxgi(Bitmap source)
        {
            if (_dxgi == null)
            {
                if (DateTime.UtcNow < _dxgiRetryAt) return false;
                try
                {
                    _dxgi = DxgiCapture.Create(_screen);
                    if (_dxgi.Width != source.Width || _dxgi.Height != source.Height)
                    {
                        throw new InvalidOperationException("DXGI output size differs from the screen");
                    }
                }
                catch
                {
                    DropDxgi();
                    return false;
                }
            }

            BitmapData data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                _dxgi.Capture(data.Scan0, data.Stride);
                return true;
            }
            catch
            {
                DropDxgi();   // e.g. access lost after a mode change or on the secure desktop
                return false;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        private void DropDxgi()
        {
            if (_dxgi != null) _dxgi.Dispose();
            _dxgi = null;
            _dxgiRetryAt = DateTime.UtcNow.AddSeconds(5);
        }

        // Graphics.CopyFromScreen rejects SRCCOPY | CAPTUREBLT as an invalid enum
        // value, so call BitBlt directly. CAPTUREBLT includes layered (transparent)
        // windows, which a plain copy leaves out.
        private void CopyScreen(Graphics graphics)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC failed");
            try
            {
                IntPtr hdc = graphics.GetHdc();
                try
                {
                    if (!BitBlt(hdc, 0, 0, _bounds.Width, _bounds.Height, screenDc, _bounds.X, _bounds.Y, SrcCopy | CaptureBlt))
                    {
                        throw new InvalidOperationException("BitBlt failed (" + Marshal.GetLastWin32Error() + ")");
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void DrawCursor(Graphics graphics)
        {
            CURSORINFO info = new CURSORINFO();
            info.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (!GetCursorInfo(ref info) || (info.flags & CursorShowing) == 0 || info.hCursor == IntPtr.Zero) return;

            // Most of the time the cursor is on another monitor; skip the work then.
            Rectangle near = Rectangle.Inflate(_bounds, 128, 128);
            if (!near.Contains(info.ptScreenPos.X, info.ptScreenPos.Y)) return;

            ICONINFO icon;
            if (!GetIconInfo(info.hCursor, out icon)) return;

            try
            {
                int x = info.ptScreenPos.X - _bounds.Left - icon.xHotspot;
                int y = info.ptScreenPos.Y - _bounds.Top - icon.yHotspot;
                IntPtr hdc = graphics.GetHdc();
                try
                {
                    DrawIconEx(hdc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, DiNormal);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
            finally
            {
                if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
                if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            }
        }

        private const int CursorShowing = 0x00000001;
        private const int DiNormal = 0x0003;
        private const int SrcCopy = 0x00CC0020;
        private const int CaptureBlt = 0x40000000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
                                          IntPtr source, int sourceX, int sourceY, int operation);

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CURSORINFO info);

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

        [DllImport("user32.dll")]
        private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int width, int height,
                                              int stepIfAniCur, IntPtr flickerFreeBrush, int flags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }
    }
}
