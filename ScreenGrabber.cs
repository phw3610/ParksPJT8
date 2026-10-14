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
        private GdiCapture _gdi;
        private WgcCapture _wgc;
        private DateTime _wgcRetryAt = DateTime.MinValue;
        private readonly int _mode;   // 0 automatic (DXGI), 1 Windows Graphics Capture, 2 GDI only

        // "DXGI", "WGC" or "GDI": which path produced the last frame.
        public string Backend { get; private set; }

        public ScreenGrabber(Screen screen, Size output, int mode)
        {
            _screen = screen;
            _bounds = screen.Bounds;
            _mode = mode;
            Backend = mode == 1 ? "WGC" : mode == 2 ? "GDI" : "DXGI";
            if (output != _bounds.Size)
            {
                _native = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppRgb);
                _scaler = new AreaScaler(_bounds.Width, _bounds.Height, output.Width, output.Height);
            }
        }

        public void Grab(Bitmap target)
        {
            Bitmap source = _native ?? target;
            bool captured = false;
            bool cursorIncluded = false;   // Windows Graphics Capture already draws the cursor
            if (_mode == 1)
            {
                captured = TryGrabWgc(source);
                cursorIncluded = captured;
                Backend = captured ? "WGC" : "GDI";
            }
            else if (_mode == 0)
            {
                captured = TryGrabDxgi(source);
                Backend = captured ? "DXGI" : "GDI";
            }
            else
            {
                Backend = "GDI";
            }

            if (!captured) GrabGdi(source);

            if (!cursorIncluded)
            {
                using (Graphics graphics = Graphics.FromImage(source)) DrawCursor(graphics);
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
            if (_gdi != null) _gdi.Dispose();
            if (_wgc != null) _wgc.Dispose();
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

        private bool TryGrabWgc(Bitmap source)
        {
            if (_wgc == null)
            {
                if (DateTime.UtcNow < _wgcRetryAt) return false;
                try
                {
                    _wgc = WgcCapture.Create(_screen);
                    if (_wgc.Width != source.Width || _wgc.Height != source.Height)
                    {
                        throw new InvalidOperationException("Windows capture size differs from the screen");
                    }
                }
                catch
                {
                    DropWgc();
                    return false;
                }
            }

            BitmapData data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                _wgc.Capture(data.Scan0, data.Stride);
                return true;
            }
            catch
            {
                DropWgc();   // e.g. the monitor changed size; rebuilt after the retry delay
                return false;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        private void DropWgc()
        {
            if (_gdi != null) _gdi.Dispose();
            if (_wgc != null) _wgc.Dispose();
            _wgc = null;
            _wgcRetryAt = DateTime.UtcNow.AddSeconds(5);
        }

        // The fallback and the "GDI" mode. Layered (transparent) windows are included.
        private void GrabGdi(Bitmap source)
        {
            if (_gdi == null) _gdi = new GdiCapture(_screen.DeviceName, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height);

            BitmapData data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                _gdi.Capture(data.Scan0, data.Stride);
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
