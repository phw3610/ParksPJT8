using System;
using System.Windows.Forms;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.DXGI.Resource;

namespace ScreenRecorder
{
    // Desktop Duplication: the GPU hands over the desktop image, which is far
    // faster than GDI (a plain BitBlt of a 2560x1440 screen took ~150 ms here, so
    // no setting could get past ~6 fps). One instance per monitor; created and used
    // on the recorder thread. Throws when duplication is unavailable (secure
    // desktop, remote session, no GPU), and the caller falls back to GDI.
    internal sealed unsafe class DxgiCapture : IDisposable
    {
        private readonly Device _device;
        private readonly DeviceContext _context;
        private readonly OutputDuplication _duplication;
        private readonly DisplayModeRotation _rotation;
        private Texture2D _staging;
        private int _textureWidth;
        private int _textureHeight;
        private bool _hasFrame;

        public readonly int Width;
        public readonly int Height;

        private DxgiCapture(Device device, OutputDuplication duplication, int width, int height)
        {
            _device = device;
            _context = device.ImmediateContext;
            _duplication = duplication;
            _rotation = duplication.Description.Rotation;
            Width = width;
            Height = height;
        }

        public static DxgiCapture Create(Screen screen)
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
                                OutputDescription description = output.Description;
                                if (!string.Equals(description.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;

                                Device device = new Device(adapter, DeviceCreationFlags.BgraSupport);
                                try
                                {
                                    using (Output1 output1 = output.QueryInterface<Output1>())
                                    {
                                        OutputDuplication duplication = output1.DuplicateOutput(device);
                                        int width = description.DesktopBounds.Right - description.DesktopBounds.Left;
                                        int height = description.DesktopBounds.Bottom - description.DesktopBounds.Top;
                                        return new DxgiCapture(device, duplication, width, height);
                                    }
                                }
                                catch
                                {
                                    device.Dispose();
                                    throw;
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

            throw new InvalidOperationException("DXGI output not found: " + screen.DeviceName);
        }

        // Copies the current desktop (BGRA, top-down) to dest. Throws when no
        // image could be produced, so the caller can use the GDI path instead.
        public void Capture(IntPtr dest, int stride)
        {
            // Duplication only delivers an image when the screen changed. Until the
            // first one arrives keep asking; after that a timeout just means "unchanged"
            // and the previous image still stands.
            for (int attempt = 0; attempt < 8 && !_hasFrame; attempt++) TryAcquire(40);
            if (_hasFrame) TryAcquire(0);

            if (!_hasFrame || _staging == null) throw new InvalidOperationException("DXGI produced no desktop image yet");
            CopyOut(dest, stride);
        }

        private void TryAcquire(int timeoutMs)
        {
            Resource desktop = null;
            bool acquired = false;
            try
            {
                OutputDuplicateFrameInformation info;
                Result result = _duplication.TryAcquireNextFrame(timeoutMs, out info, out desktop);
                if (result.Success)
                {
                    acquired = true;
                    bool changed = info.AccumulatedFrames > 0 || info.LastPresentTime != 0;
                    if (changed || !_hasFrame)
                    {
                        using (Texture2D texture = desktop.QueryInterface<Texture2D>())
                        {
                            EnsureStaging(texture.Description);
                            _context.CopyResource(texture, _staging);
                        }

                        if (changed) _hasFrame = true;
                    }
                }
                else if (result != SharpDX.DXGI.ResultCode.WaitTimeout)
                {
                    throw new SharpDXException(result);
                }
            }
            finally
            {
                if (desktop != null) desktop.Dispose();
                if (acquired) _duplication.ReleaseFrame();
            }
        }

        // Sized from the texture DXGI actually delivers: on a rotated monitor the
        // description reports the rotated size but the texture arrives unrotated.
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
            DataBox source = _context.MapSubresource(_staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try
            {
                byte* from = (byte*)source.DataPointer;
                byte* to = (byte*)dest;

                if (_rotation == DisplayModeRotation.Rotate90 || _rotation == DisplayModeRotation.Rotate180 || _rotation == DisplayModeRotation.Rotate270)
                {
                    CopyRotated(from, source.RowPitch, to, stride);
                    return;
                }

                if (_textureWidth != Width || _textureHeight != Height)
                {
                    throw new InvalidOperationException("DXGI texture size does not match the output");
                }

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

        // Turns the unrotated duplication texture back into the orientation the
        // user sees, so a portrait monitor is not sideways.
        private void CopyRotated(byte* from, int rowPitch, byte* to, int stride)
        {
            for (int y = 0; y < Height; y++)
            {
                uint* row = (uint*)(to + (long)y * stride);
                for (int x = 0; x < Width; x++)
                {
                    int sourceX, sourceY;
                    if (_rotation == DisplayModeRotation.Rotate90)
                    {
                        sourceX = y;
                        sourceY = _textureHeight - 1 - x;
                    }
                    else if (_rotation == DisplayModeRotation.Rotate270)
                    {
                        sourceX = _textureWidth - 1 - y;
                        sourceY = x;
                    }
                    else
                    {
                        sourceX = _textureWidth - 1 - x;
                        sourceY = _textureHeight - 1 - y;
                    }

                    if (sourceX < 0 || sourceY < 0 || sourceX >= _textureWidth || sourceY >= _textureHeight)
                    {
                        row[x] = 0xFF000000u;
                        continue;
                    }

                    row[x] = *(uint*)(from + (long)sourceY * rowPitch + (long)sourceX * 4);
                }
            }
        }

        public void Dispose()
        {
            if (_staging != null) _staging.Dispose();
            _duplication.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
