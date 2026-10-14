using System;
using System.Runtime.InteropServices;

namespace ScreenRecorder
{
    // GDI screen copy, the capture path that works when Desktop Duplication and Windows
    // Graphics Capture return black. Copying from the monitor's own display DC
    // (CreateDC "DISPLAY") instead of the whole-desktop screen DC from GetDC(NULL) is about
    // 4x faster (35 ms against 150 ms for 2560x1440 here), and copying into a plain DIB
    // section instead of a GDI+ bitmap's device context avoids more overhead. Splitting the
    // copy over several threads was measured and is slower.
    internal sealed unsafe class GdiCapture : IDisposable
    {
        private const int SrcCopy = 0x00CC0020;
        private const int CaptureBlt = 0x40000000;   // also copy layered (transparent) windows

        private readonly string _device;   // the monitor's device name, e.g. DISPLAY1
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private IntPtr _oldBitmap;
        private IntPtr _bits;

        public GdiCapture(string device, int x, int y, int width, int height)
        {
            _device = device;
            _x = x;
            _y = y;
            _width = width;
            _height = height;

            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new InvalidOperationException("GetDC failed");
            try
            {
                _memoryDc = CreateCompatibleDC(screen);
                if (_memoryDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed");

                BITMAPINFOHEADER info = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = width, biHeight = -height,   // negative = top-down
                    biPlanes = 1, biBitCount = 32
                };
                _bitmap = CreateDIBSection(screen, ref info, 0, out _bits, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
                _oldBitmap = SelectObject(_memoryDc, _bitmap);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // Copies the screen area (BGRX, top-down) to dest. Throws when the copy fails.
        public void Capture(IntPtr dest, int stride)
        {
            bool monitorDc = false;
            IntPtr source = IntPtr.Zero;
            if (!string.IsNullOrEmpty(_device))
            {
                source = CreateDC("DISPLAY", _device, null, IntPtr.Zero);
                monitorDc = source != IntPtr.Zero;
            }

            if (!monitorDc) source = GetDC(IntPtr.Zero);   // fallback: the whole-desktop screen DC
            if (source == IntPtr.Zero) throw new InvalidOperationException("no device context for the screen");

            try
            {
                // A monitor DC starts at that monitor's own corner; the screen DC at the desktop's.
                if (!BitBlt(_memoryDc, 0, 0, _width, _height, source, monitorDc ? 0 : _x, monitorDc ? 0 : _y, SrcCopy | CaptureBlt))
                {
                    throw new InvalidOperationException("BitBlt failed (" + Marshal.GetLastWin32Error() + ")");
                }
            }
            finally
            {
                if (monitorDc) DeleteDC(source);
                else ReleaseDC(IntPtr.Zero, source);
            }

            byte* from = (byte*)_bits;
            byte* to = (byte*)dest;
            long rowBytes = (long)_width * 4;
            for (int row = 0; row < _height; row++)
            {
                System.Buffer.MemoryCopy(from + row * rowBytes, to + (long)row * stride, rowBytes, rowBytes);
            }
        }

        public void Dispose()
        {
            if (_memoryDc != IntPtr.Zero)
            {
                if (_oldBitmap != IntPtr.Zero) SelectObject(_memoryDc, _oldBitmap);
                DeleteDC(_memoryDc);
            }

            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            _memoryDc = _bitmap = _oldBitmap = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDC(string driver, string device, string port, IntPtr initData);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
                                          IntPtr source, int sourceX, int sourceY, int operation);
    }
}
