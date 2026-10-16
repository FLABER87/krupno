namespace Krupno.Core.Imaging;

/// <summary>
/// Lanczos-3 resampling: separable passes, in bands of output rows,
/// so the intermediate buffer doesn't grow with the image.
/// </summary>
public static class Resampler
{
    private const int BandRows = 64;
    private const double Support = 3.0;
    private const float Inv255 = 1f / 255f;

    public static ImageBuffer Resize(ImageBuffer source, int width, int height, CancellationToken cancellationToken = default)
    {
        var result = new ImageBuffer(width, height);
        Resize(source.Pixels, source.Width, source.Height, ImageBuffer.BytesPerPixel, result.Pixels, width, height,
            premultiply: false, cancellationToken);
        return result;
    }

    /// <summary>
    /// Resizes an image with transparency: colors are weighted by alpha while filtering, so a nearly transparent pixel
    /// doesn't pull its color into its neighbors. The result has straight (not premultiplied) colors.
    /// </summary>
    public static ImageBuffer ResizePremultiplied(ImageBuffer source, int width, int height, CancellationToken cancellationToken = default)
    {
        var result = new ImageBuffer(width, height);
        Resize(source.Pixels, source.Width, source.Height, ImageBuffer.BytesPerPixel, result.Pixels, width, height,
            premultiply: true, cancellationToken);
        return result;
    }

    /// <summary>Resizes a single-channel plane (for example, alpha).</summary>
    public static byte[] ResizePlane(byte[] plane, int width, int height, int newWidth, int newHeight,
        CancellationToken cancellationToken = default)
    {
        var result = GC.AllocateUninitializedArray<byte>(newWidth * newHeight);
        Resize(plane, width, height, 1, result, newWidth, newHeight, premultiply: false, cancellationToken);
        return result;
    }

    private static void Resize(byte[] src, int srcW, int srcH, int channels, byte[] dst, int dstW, int dstH, bool premultiply,
        CancellationToken cancellationToken)
    {
        var horizontal = Kernel.Create(srcW, dstW);
        var vertical = Kernel.Create(srcH, dstH);
        int bandCount = (dstH + BandRows - 1) / BandRows;
        int rowLength = dstW * channels;

        // One intermediate buffer per worker, sized for the tallest band: a buffer per band would allocate gigabytes
        // on the large object heap for a big result.
        int maxBandRows = 0;
        for (int band = 0; band < bandCount; band++)
        {
            var (firstRow, lastRow) = SourceRows(vertical, band, dstH);
            maxBandRows = Math.Max(maxBandRows, lastRow - firstRow);
        }

        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.For(0, bandCount, parallelOptions, () => new float[maxBandRows * rowLength], (band, _, temp) =>
        {
            int y0 = band * BandRows;
            int y1 = Math.Min(dstH, y0 + BandRows);
            var (firstRow, lastRow) = SourceRows(vertical, band, dstH);

            // Horizontal pass, only for the rows needed.
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
                        if (premultiply && c < 3)
                        {
                            // The alpha byte of the same pixel is the last one (BGRA).
                            for (int k = 0; k < count; k++, s += channels)
                                sum += horizontal.Weights[weights + k] * src[s] * (src[s - c + 3] * Inv255);
                        }
                        else
                        {
                            for (int k = 0; k < count; k++, s += channels)
                                sum += horizontal.Weights[weights + k] * src[s];
                        }
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
                if (premultiply)
                {
                    for (int x = 0; x < rowLength; x += channels)
                    {
                        float b = 0, g = 0, r = 0, a = 0;
                        int t = (start - firstRow) * rowLength + x;
                        for (int k = 0; k < count; k++, t += rowLength)
                        {
                            float w = vertical.Weights[weights + k];
                            b += w * temp[t];
                            g += w * temp[t + 1];
                            r += w * temp[t + 2];
                            a += w * temp[t + 3];
                        }
                        // Back to straight colors; where the pixel is (almost) fully transparent, the color doesn't matter.
                        float unpremultiply = a >= 0.5f ? 255f / a : 0;
                        dst[dstRow + x] = ToByte(b * unpremultiply);
                        dst[dstRow + x + 1] = ToByte(g * unpremultiply);
                        dst[dstRow + x + 2] = ToByte(r * unpremultiply);
                        dst[dstRow + x + 3] = ToByte(a);
                    }
                }
                else
                {
                    for (int i = 0; i < rowLength; i++)
                    {
                        float sum = 0;
                        int t = (start - firstRow) * rowLength + i;
                        for (int k = 0; k < count; k++, t += rowLength)
                            sum += vertical.Weights[weights + k] * temp[t];
                        dst[dstRow + i] = ToByte(sum);
                    }
                }
            }
            return temp;
        }, _ => { });
    }

    /// <summary>The source rows a band of output rows needs.</summary>
    private static (int First, int Last) SourceRows(Kernel vertical, int band, int dstH)
    {
        int y0 = band * BandRows;
        int y1 = Math.Min(dstH, y0 + BandRows);
        int firstRow = int.MaxValue, lastRow = 0;
        for (int y = y0; y < y1; y++)
        {
            firstRow = Math.Min(firstRow, vertical.Start[y]);
            lastRow = Math.Max(lastRow, vertical.Start[y] + vertical.Count[y]);
        }
        return (firstRow, lastRow);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);

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
