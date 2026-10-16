namespace Krupno.Core.Imaging;

/// <summary>
/// Lanczos-3 resampling: separable passes, in bands of output rows,
/// so the intermediate buffer doesn't grow with the image.
/// </summary>
public static class Resampler
{
    private const int BandRows = 64;
    private const double Support = 3.0;

    public static ImageBuffer Resize(ImageBuffer source, int width, int height)
    {
        var result = new ImageBuffer(width, height);
        Resize(source.Pixels, source.Width, source.Height, ImageBuffer.BytesPerPixel, result.Pixels, width, height);
        return result;
    }

    /// <summary>Resizes a single-channel plane (for example, alpha).</summary>
    public static byte[] ResizePlane(byte[] plane, int width, int height, int newWidth, int newHeight)
    {
        var result = GC.AllocateUninitializedArray<byte>(newWidth * newHeight);
        Resize(plane, width, height, 1, result, newWidth, newHeight);
        return result;
    }

    private static void Resize(byte[] src, int srcW, int srcH, int channels, byte[] dst, int dstW, int dstH)
    {
        var horizontal = Kernel.Create(srcW, dstW);
        var vertical = Kernel.Create(srcH, dstH);
        int bandCount = (dstH + BandRows - 1) / BandRows;

        Parallel.For(0, bandCount, band =>
        {
            int y0 = band * BandRows;
            int y1 = Math.Min(dstH, y0 + BandRows);

            // Which source rows this band needs.
            int firstRow = int.MaxValue, lastRow = 0;
            for (int y = y0; y < y1; y++)
            {
                firstRow = Math.Min(firstRow, vertical.Start[y]);
                lastRow = Math.Max(lastRow, vertical.Start[y] + vertical.Count[y]);
            }

            // Horizontal pass, only for the rows needed.
            int rowLength = dstW * channels;
            var temp = new float[(lastRow - firstRow) * rowLength];
            for (int sy = firstRow; sy < lastRow; sy++)
            {
                int srcRow = sy * srcW * channels;
                int tempRow = (sy - firstRow) * rowLength;
                for (int x = 0; x < dstW; x++)
                {
                    int start = horizontal.Start[x], count = horizontal.Count[x];
                    int weights = x * horizontal.MaxCount;
                    for (int c = 0; c < channels; c++)
                    {
                        float sum = 0;
                        int s = srcRow + start * channels + c;
                        for (int k = 0; k < count; k++, s += channels)
                            sum += horizontal.Weights[weights + k] * src[s];
                        temp[tempRow + x * channels + c] = sum;
                    }
                }
            }

            // Vertical pass.
            for (int y = y0; y < y1; y++)
            {
                int start = vertical.Start[y], count = vertical.Count[y];
                int weights = y * vertical.MaxCount;
                int dstRow = y * rowLength;
                for (int i = 0; i < rowLength; i++)
                {
                    float sum = 0;
                    int t = (start - firstRow) * rowLength + i;
                    for (int k = 0; k < count; k++, t += rowLength)
                        sum += vertical.Weights[weights + k] * temp[t];
                    dst[dstRow + i] = (byte)Math.Clamp((int)(sum + 0.5f), 0, 255);
                }
            }
        });
    }

    private static double Lanczos(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-8)
            return 1;
        if (x >= Support)
            return 0;
        double px = Math.PI * x;
        return Support * Math.Sin(px) * Math.Sin(px / Support) / (px * px);
    }

    private sealed class Kernel
    {
        public required int[] Start { get; init; }
        public required int[] Count { get; init; }
        public required float[] Weights { get; init; }
        public required int MaxCount { get; init; }

        public static Kernel Create(int srcSize, int dstSize)
        {
            double scale = (double)dstSize / srcSize;
            // When downscaling, the filter is stretched; otherwise there would be aliasing.
            double filterScale = Math.Max(1.0, 1.0 / scale);
            double support = Support * filterScale;
            int maxCount = (int)Math.Ceiling(support * 2) + 1;

            var start = new int[dstSize];
            var count = new int[dstSize];
            var weights = new float[dstSize * maxCount];
            var temp = new double[maxCount];

            for (int i = 0; i < dstSize; i++)
            {
                double center = (i + 0.5) / scale;
                int left = Math.Max(0, (int)Math.Floor(center - support));
                int right = Math.Min(srcSize, (int)Math.Ceiling(center + support));
                int n = Math.Min(right - left, maxCount);

                double sum = 0;
                for (int k = 0; k < n; k++)
                {
                    temp[k] = Lanczos((left + k + 0.5 - center) / filterScale);
                    sum += temp[k];
                }
                for (int k = 0; k < n; k++)
                    weights[i * maxCount + k] = (float)(sum != 0 ? temp[k] / sum : 1.0 / n);

                start[i] = left;
                count[i] = n;
            }

            return new Kernel { Start = start, Count = count, Weights = weights, MaxCount = maxCount };
        }
    }
}
