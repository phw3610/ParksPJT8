using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Windows.Forms;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenRecorder
{
    // Windows Graphics Capture: the capture path the Windows shell itself offers
    // (Windows 10 1903+). It takes the image from the compositor like Desktop
    // Duplication does, but through a different route, so it can show windows
    // that duplication returns black (some GPU-accelerated windows, hybrid-GPU
    // setups). Protected (DRM) content stays black on purpose; nothing here tries
    // to get around that. The system draws the mouse cursor itself.
    internal sealed unsafe class WgcCapture : IDisposable
    {
        private static readonly Guid GraphicsCaptureItemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid Texture2DId = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        private readonly Device _device;
        private readonly DeviceContext _context;
        private readonly GraphicsCaptureItem _item;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private Texture2D _staging;
        private int _textureWidth;
        private int _textureHeight;
        private bool _hasFrame;

        public readonly int Width;
        public readonly int Height;

        private WgcCapture(Device device, GraphicsCaptureItem item, Direct3D11CaptureFramePool pool,
                           GraphicsCaptureSession session, int width, int height)
        {
            _device = device;
            _context = device.ImmediateContext;
            _item = item;
            _pool = pool;
            _session = session;
            Width = width;
            Height = height;
        }

        // Created and used on the recorder thread. Throws when Windows Graphics Capture is
        // not available (older Windows, remote session), and the caller falls back to GDI.
        public static WgcCapture Create(Screen screen)
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture is not supported");

            Device device = CreateDevice(screen);
            try
            {
                IntPtr monitor = MonitorFromPoint(
                    new POINT { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + screen.Bounds.Height / 2 }, 2);
                GraphicsCaptureItem item = CreateItem(monitor);

                IDirect3DDevice winrtDevice = CreateWinRtDevice(device);
                Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
                GraphicsCaptureSession session = pool.CreateCaptureSession(item);
                try { session.IsBorderRequired = false; }   // no yellow frame where the OS allows it
                catch { }
                session.StartCapture();
                return new WgcCapture(device, item, pool, session, item.Size.Width, item.Size.Height);
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }

        // The device is made on the GPU that drives this monitor, so frames need no cross-GPU copy.
        private static Device CreateDevice(Screen screen)
        {
            using (Factory1 factory = new Factory1())
            {
                foreach (Adapter1 adapter in factory.Adapters1)
                {
                    try
                    {
                        foreach (Output output in adapter.Outputs)
                        {
                            try
                            {
                                if (string.Equals(output.Description.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return new Device(adapter, DeviceCreationFlags.BgraSupport);
                                }
                            }
                            finally
                            {
                                output.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        adapter.Dispose();
                    }
                }
            }

            return new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }

        private static GraphicsCaptureItem CreateItem(IntPtr monitor)
        {
            IGraphicsCaptureItemInterop interop = (IGraphicsCaptureItemInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
            Guid id = GraphicsCaptureItemId;
            IntPtr pointer = interop.CreateForMonitor(monitor, ref id);
            try { return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
        }

        private static IDirect3DDevice CreateWinRtDevice(Device device)
        {
            using (SharpDX.DXGI.Device dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device>())
            {
                IntPtr pointer;
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out pointer);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try { return (IDirect3DDevice)Marshal.GetObjectForIUnknown(pointer); }
                finally { Marshal.Release(pointer); }
            }
        }

        // Copies the newest frame (BGRA, top-down) to dest. A static screen produces no new
        // frames, so the previous image stays. Throws when nothing arrives or the size changed,
        // so the caller can rebuild the capture or use GDI.
        public void Capture(IntPtr dest, int stride)
        {
            Direct3D11CaptureFrame latest = null;
            for (int wait = 0; wait < 60; wait++)
            {
                Direct3D11CaptureFrame frame;
                while ((frame = _pool.TryGetNextFrame()) != null)
                {
                    if (latest != null) latest.Dispose();
                    latest = frame;
                }

                if (latest != null || _hasFrame) break;
                Thread.Sleep(10);
            }

            if (latest != null)
            {
                try
                {
                    if (latest.ContentSize.Width != Width || latest.ContentSize.Height != Height)
                    {
                        throw new InvalidOperationException("Windows capture size changed");
                    }

                    CopyFrame(latest);
                    _hasFrame = true;
                }
                finally
                {
                    latest.Dispose();
                }
            }

            if (!_hasFrame || _staging == null) throw new InvalidOperationException("Windows capture produced no image yet");
            CopyOut(dest, stride);
        }

        private void CopyFrame(Direct3D11CaptureFrame frame)
        {
            IDirect3DDxgiInterfaceAccess access = (IDirect3DDxgiInterfaceAccess)frame.Surface;
            Guid id = Texture2DId;
            // GetInterface hands over one reference; the SharpDX wrapper owns it and releases it on Dispose.
            using (Texture2D texture = new Texture2D(access.GetInterface(ref id)))
            {
                EnsureStaging(texture.Description);
                _context.CopyResource(texture, _staging);
            }
        }

        private void EnsureStaging(Texture2DDescription source)
        {
            if (_staging != null && _textureWidth == source.Width && _textureHeight == source.Height) return;
            if (_staging != null) _staging.Dispose();
            _textureWidth = source.Width;
            _textureHeight = source.Height;
            _staging = new Texture2D(_device, new Texture2DDescription
            {
                Width = _textureWidth,
                Height = _textureHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            });
        }

        private void CopyOut(IntPtr dest, int stride)
        {
            if (_textureWidth < Width || _textureHeight < Height) throw new InvalidOperationException("Windows capture texture is smaller than the monitor");

            DataBox source = _context.MapSubresource(_staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try
            {
                byte* from = (byte*)source.DataPointer;
                byte* to = (byte*)dest;
                long rowBytes = (long)Width * 4;
                for (int y = 0; y < Height; y++)
                {
                    System.Buffer.MemoryCopy(from + (long)y * source.RowPitch, to + (long)y * stride, rowBytes, rowBytes);
                }
            }
            finally
            {
                _context.UnmapSubresource(_staging, 0);
            }
        }

        public void Dispose()
        {
            try { _session.Dispose(); }
            catch { }
            try { _pool.Dispose(); }
            catch { }
            if (_staging != null) _staging.Dispose();
            _context.Dispose();
            _device.Dispose();
        }

        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
            IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
        }

        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT point, int flags);

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    }
}
