using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ScreenRecorder
{
    // Writes H.264 into an MP4 with the encoder Windows already ships, reached
    // through the Media Foundation sink writer. Nothing is bundled: mfplat and
    // mfreadwrite are OS components. They are missing on N editions (without the
    // Media Feature Pack), which is why TryCreate reports failure instead of throwing.
    internal sealed class Mp4Writer : IDisposable
    {
        private const int MfVersion = 0x00020070;         // MF_SDK_VERSION << 16 | MF_API_VERSION
        private const int MfStartupLite = 1;
        private const long OneSecond = 10000000L;         // 100 ns units

        private static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");

        private static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new Guid("c9173739-5e56-461c-b713-46fb995cb95f");
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

        private static readonly Guid MF_LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");

        private const int MFVideoInterlace_Progressive = 2;

        private static readonly object StartupSync = new object();
        private static int _startupCount;

        private readonly IMFSinkWriter _writer;
        private readonly int _streamIndex;
        private readonly int _width;
        private readonly int _height;
        private readonly long _frameDuration;
        private readonly int _stride;
        private readonly Stopwatch _clock = new Stopwatch();
        private long _lastTime = -1;
        private long _frames;
        private bool _closed;

        public string Path { get; private set; }
        public int Frames { get { return (int)_frames; } }

        // The sink writer buffers, so the file on disk lags behind. That is close
        // enough for the size-limit check, which only needs to spot a limit.
        public long Length
        {
            get
            {
                try { return new FileInfo(Path).Length; }
                catch { return 0; }
            }
        }

        private Mp4Writer(IMFSinkWriter writer, int streamIndex, string path, int width, int height, int fps)
        {
            _writer = writer;
            _streamIndex = streamIndex;
            Path = path;
            _width = width;
            _height = height;
            _frameDuration = OneSecond / fps;
            _stride = width * 4;
        }

        // Returns null (with a reason) when Media Foundation or the H.264 encoder is unavailable.
        // A GPU encoder is tried first: the Microsoft software encoder alone holds
        // about 1.1 GB for a 2560x1440 screen even in low-latency mode's absence and
        // keeps a CPU core busy. Where the GPU encoder is missing or refuses the
        // format, the software encoder takes over.
        public static Mp4Writer TryCreate(string path, int width, int height, int fps, int bitrate, out string error)
        {
            Mp4Writer writer = TryCreate(path, width, height, fps, bitrate, true, out error);
            if (writer != null) return writer;
            return TryCreate(path, width, height, fps, bitrate, false, out error);
        }

        private static Mp4Writer TryCreate(string path, int width, int height, int fps, int bitrate, bool hardware, out string error)
        {
            error = null;

            // H.264 needs even dimensions.
            width &= ~1;
            height &= ~1;
            if (width < 2 || height < 2)
            {
                error = "해상도가 너무 작습니다.";
                return null;
            }

            IMFSinkWriter writer = null;
            try
            {
                Startup();

                int hr = CreateSinkWriter(path, hardware, out writer);
                if (hr < 0) throw Marshal.GetExceptionForHR(hr);

                IMFMediaType outputType;
                hr = MFCreateMediaType(out outputType);
                if (hr < 0) throw Marshal.GetExceptionForHR(hr);
                outputType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                outputType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
                outputType.SetUINT32(MF_MT_AVG_BITRATE, bitrate);
                outputType.SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                outputType.SetUINT64(MF_MT_FRAME_SIZE, Pack(width, height));
                outputType.SetUINT64(MF_MT_FRAME_RATE, Pack(fps, 1));
                outputType.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));

                int streamIndex;
                hr = writer.AddStream(outputType, out streamIndex);
                Marshal.ReleaseComObject(outputType);
                if (hr < 0) throw Marshal.GetExceptionForHR(hr);

                // RGB32 in: the sink writer inserts the colour converter itself.
                IMFMediaType inputType;
                hr = MFCreateMediaType(out inputType);
                if (hr < 0) throw Marshal.GetExceptionForHR(hr);
                inputType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                inputType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                inputType.SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                inputType.SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                // GDI hands us top-down rows, so declare a positive stride.
                inputType.SetUINT32(MF_MT_DEFAULT_STRIDE, width * 4);
                inputType.SetUINT64(MF_MT_FRAME_SIZE, Pack(width, height));
                inputType.SetUINT64(MF_MT_FRAME_RATE, Pack(fps, 1));
                inputType.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));

                hr = writer.SetInputMediaType(streamIndex, inputType, IntPtr.Zero);
                Marshal.ReleaseComObject(inputType);
                if (hr < 0) throw Marshal.GetExceptionForHR(hr);

                hr = writer.BeginWriting();
                if (hr < 0) throw Marshal.GetExceptionForHR(hr);

                Mp4Writer result = new Mp4Writer(writer, streamIndex, path, width, height, fps);
                result._clock.Start();
                return result;
            }
            catch (Exception ex)
            {
                if (writer != null)
                {
                    try { Marshal.ReleaseComObject(writer); }
                    catch { }
                }

                Shutdown();
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
                error = ex.Message;
                return null;
            }
        }

        // The frame must be the size this writer was created with. Frames are
        // stamped with the real time they arrive, so a slow encoder drops the
        // frame rate instead of making the video play faster than it was recorded.
        public void AddFrame(Bitmap frame)
        {
            if (_closed) return;

            long time = _clock.ElapsedTicks * OneSecond / Stopwatch.Frequency;
            if (time <= _lastTime) time = _lastTime + 1;
            _lastTime = time;

            int bufferLength = _stride * _height;
            IMFMediaBuffer buffer;
            int hr = MFCreateMemoryBuffer(bufferLength, out buffer);
            if (hr < 0) throw Marshal.GetExceptionForHR(hr);

            IMFSample sample = null;
            try
            {
                IntPtr target;
                int maxLength, currentLength;
                hr = buffer.Lock(out target, out maxLength, out currentLength);
                if (hr < 0) throw Marshal.GetExceptionForHR(hr);

                try
                {
                    BitmapData data = frame.LockBits(new Rectangle(0, 0, _width, _height),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        // Row by row: the bitmap's own stride may be padded, the buffer's is not.
                        for (int y = 0; y < _height; y++)
                        {
                            CopyMemory(new IntPtr(target.ToInt64() + (long)y * _stride),
                                       new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride),
                                       (UIntPtr)(uint)_stride);
                        }
                    }
                    finally
                    {
                        frame.UnlockBits(data);
                    }
                }
                finally
                {
                    buffer.Unlock();
                }

                buffer.SetCurrentLength(bufferLength);

                hr = MFCreateSample(out sample);
                if (hr < 0) throw Marshal.GetExceptionForHR(hr);
                sample.AddBuffer(buffer);
                sample.SetSampleTime(time);
                sample.SetSampleDuration(_frameDuration);

                hr = _writer.WriteSample(_streamIndex, sample);
                if (hr < 0) throw Marshal.GetExceptionForHR(hr);
                _frames++;
            }
            finally
            {
                if (sample != null) Marshal.ReleaseComObject(sample);
                Marshal.ReleaseComObject(buffer);
            }
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try { _writer.Finalize_(); }
            catch { }
            try { Marshal.ReleaseComObject(_writer); }
            catch { }
            Shutdown();
        }

        private static long Pack(int high, int low)
        {
            return ((long)high << 32) | (uint)low;
        }

        // Low latency stops the encoder from buffering dozens of raw frames ahead
        // (that was ~800 MB of the 1.1 GB); the other attribute allows a GPU encoder.
        private static int CreateSinkWriter(string path, bool hardware, out IMFSinkWriter writer)
        {
            IMFAttributes attributes;
            int hr = MFCreateAttributes(out attributes, 2);
            if (hr < 0) throw Marshal.GetExceptionForHR(hr);

            IntPtr pointer = IntPtr.Zero;
            try
            {
                attributes.SetUINT32(MF_LOW_LATENCY, 1);
                if (hardware) attributes.SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                pointer = Marshal.GetComInterfaceForObject(attributes, typeof(IMFAttributes));
                return MFCreateSinkWriterFromURL(path, IntPtr.Zero, pointer, out writer);
            }
            finally
            {
                if (pointer != IntPtr.Zero) Marshal.Release(pointer);
                Marshal.ReleaseComObject(attributes);
            }
        }

        private static void Startup()
        {
            lock (StartupSync)
            {
                if (_startupCount == 0)
                {
                    int hr = MFStartup(MfVersion, MfStartupLite);
                    if (hr < 0) throw Marshal.GetExceptionForHR(hr);
                }

                _startupCount++;
            }
        }

        private static void Shutdown()
        {
            lock (StartupSync)
            {
                if (_startupCount == 0) return;
                _startupCount--;
                if (_startupCount == 0)
                {
                    try { MFShutdown(); }
                    catch { }
                }
            }
        }

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType(out IMFMediaType type);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateSample(out IMFSample sample);

        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IMFSinkWriter writer);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);

        // Only the methods this file calls are typed; the rest are placeholders that
        // keep the vtable slots in order. Never call a placeholder.
        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            void Slot01(); void Slot02(); void Slot03(); void Slot04();
            void Slot05(); void Slot06(); void Slot07(); void Slot08();
            void Slot09(); void Slot10(); void Slot11(); void Slot12();
            void Slot13(); void Slot14(); void Slot15(); void Slot16();
            void Slot17(); void Slot18();
            void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            void Slot01(); void Slot02(); void Slot03(); void Slot04();
            void Slot05(); void Slot06(); void Slot07(); void Slot08();
            void Slot09(); void Slot10(); void Slot11(); void Slot12();
            void Slot13(); void Slot14(); void Slot15(); void Slot16();
            void Slot17(); void Slot18();
            void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
            void SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
            void Slot21();
            void SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
        }

        [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out int length);
            [PreserveSig] int SetCurrentLength(int length);
            [PreserveSig] int GetMaxLength(out int length);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            // IMFAttributes slots first (IMFSample derives from it).
            void Slot01(); void Slot02(); void Slot03(); void Slot04();
            void Slot05(); void Slot06(); void Slot07(); void Slot08();
            void Slot09(); void Slot10(); void Slot11(); void Slot12();
            void Slot13(); void Slot14(); void Slot15(); void Slot16();
            void Slot17(); void Slot18(); void Slot19(); void Slot20();
            void Slot21(); void Slot22(); void Slot23(); void Slot24();
            void Slot25(); void Slot26(); void Slot27(); void Slot28();
            void Slot29(); void Slot30();
            // IMFSample's own methods
            [PreserveSig] int GetSampleFlags(out int flags);
            [PreserveSig] int SetSampleFlags(int flags);
            [PreserveSig] int GetSampleTime(out long time);
            [PreserveSig] int SetSampleTime(long time);
            [PreserveSig] int GetSampleDuration(out long duration);
            [PreserveSig] int SetSampleDuration(long duration);
            [PreserveSig] int GetBufferCount(out int count);
            [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        }

        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSinkWriter
        {
            [PreserveSig] int AddStream(IMFMediaType targetMediaType, out int streamIndex);
            [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType inputMediaType, IntPtr encodingParameters);
            [PreserveSig] int BeginWriting();
            [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
            [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
            [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
            [PreserveSig] int NotifyEndOfSegment(int streamIndex);
            [PreserveSig] int Flush(int streamIndex);
            // "Finalize" collides with Object.Finalize, so the managed name differs;
            // only the vtable position matters.
            [PreserveSig] int Finalize_();
        }
    }
}
