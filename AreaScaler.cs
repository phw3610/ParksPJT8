using System;
using System.Threading.Tasks;

namespace ScreenRecorder
{
    // Area-average downscale of BGRX pixels: every output pixel is the exact mean
    // of the source area it covers, which keeps small text readable (GDI+ bicubic
    // does the same job at 35-47 ms a frame on 2560x1440, which capped scaled
    // recordings near 20 fps). Rows are split across cores.
    internal sealed unsafe class AreaScaler
    {
        private readonly int _sourceWidth;
        private readonly int _destWidth;
        private readonly int _destHeight;
        private readonly Taps _x;
        private readonly Taps _y;

        // For output index i: source indexes Start[i] .. Start[i] + Weights[i].Length - 1.
        private sealed class Taps
        {
            public int[] Start;
            public float[][] Weights;
        }

        public AreaScaler(int sourceWidth, int sourceHeight, int destWidth, int destHeight)
        {
            _sourceWidth = sourceWidth;
            _destWidth = destWidth;
            _destHeight = destHeight;
            _x = Build(sourceWidth, destWidth);
            _y = Build(sourceHeight, destHeight);
        }

        private static Taps Build(int source, int dest)
        {
            double ratio = (double)source / dest;
            Taps taps = new Taps { Start = new int[dest], Weights = new float[dest][] };
            for (int i = 0; i < dest; i++)
            {
                double from = i * ratio;
                double to = (i + 1) * ratio;
                int first = (int)Math.Floor(from);
                int last = Math.Min(source - 1, (int)Math.Ceiling(to) - 1);
                float[] weights = new float[last - first + 1];
                for (int k = 0; k < weights.Length; k++)
                {
                    double overlap = Math.Min(to, first + k + 1) - Math.Max(from, first + k);
                    weights[k] = (float)(Math.Max(0, overlap) / ratio);
                }

                taps.Start[i] = first;
                taps.Weights[i] = weights;
            }

            return taps;
        }

        public void Scale(IntPtr source, int sourceStride, IntPtr dest, int destStride)
        {
            int bands = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
            Parallel.For(0, bands, band =>
            {
                int y0 = band * _destHeight / bands;
                int y1 = (band + 1) * _destHeight / bands;
                ScaleRows((byte*)source, sourceStride, (byte*)dest, destStride, y0, y1);
            });
        }

        private void ScaleRows(byte* source, int sourceStride, byte* dest, int destStride, int y0, int y1)
        {
            float[] sums = new float[_sourceWidth * 3];
            fixed (float* acc = sums)
            {
                for (int y = y0; y < y1; y++)
                {
                    // vertical: weighted sum of the source rows this output row covers
                    for (int i = 0; i < _sourceWidth * 3; i++) acc[i] = 0;
                    float[] rowWeights = _y.Weights[y];
                    for (int k = 0; k < rowWeights.Length; k++)
                    {
                        byte* row = source + (long)(_y.Start[y] + k) * sourceStride;
                        float w = rowWeights[k];
                        for (int x = 0; x < _sourceWidth; x++)
                        {
                            acc[3 * x] += row[4 * x] * w;
                            acc[3 * x + 1] += row[4 * x + 1] * w;
                            acc[3 * x + 2] += row[4 * x + 2] * w;
                        }
                    }

                    // horizontal: weighted sum of the columns each output pixel covers
                    byte* output = dest + (long)y * destStride;
                    for (int x = 0; x < _destWidth; x++)
                    {
                        float[] colWeights = _x.Weights[x];
                        int index = _x.Start[x] * 3;
                        float b = 0, g = 0, r = 0;
                        for (int k = 0; k < colWeights.Length; k++)
                        {
                            float w = colWeights[k];
                            b += acc[index] * w;
                            g += acc[index + 1] * w;
                            r += acc[index + 2] * w;
                            index += 3;
                        }

                        output[4 * x] = (byte)(b + 0.5f);
                        output[4 * x + 1] = (byte)(g + 0.5f);
                        output[4 * x + 2] = (byte)(r + 0.5f);
                        output[4 * x + 3] = 255;
                    }
                }
            }
        }
    }
}
