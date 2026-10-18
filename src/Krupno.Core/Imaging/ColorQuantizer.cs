namespace Krupno.Core.Imaging;

/// <summary>
/// Reduces a frame to a palette of at most 256 colors for GIF. A frame with that few colors keeps them exactly; otherwise
/// Xiaolin Wu's quantizer (greedy orthogonal bipartition of RGB space for variance minimization, 1991) picks the palette, and
/// Floyd–Steinberg dithering spreads the error. A pixel with alpha below 128 takes the transparent index, if asked for.
/// </summary>
internal static class ColorQuantizer
{
    /// <param name="pixels">BGRA.</param>
    /// <param name="transparent">Reserve the last index for transparent pixels; otherwise every pixel counts as opaque.</param>
    /// <returns>The palette as BGR triples, and an index for each pixel; with <paramref name="transparent"/>, index
    /// <c>palette.Length / 3</c> (one past the colors) is the transparent one.</returns>
    public static (byte[] Palette, byte[] Indices, int TransparentIndex) Quantize(ReadOnlySpan<byte> pixels, int width, int height,
        bool transparent)
    {
        int maxColors = transparent ? 255 : 256;
        int count = width * height;
        var indices = new byte[count];

        // Few colors: the palette is the colors themselves, mapped exactly.
        var exact = new Dictionary<int, byte>();
        bool fits = true;
        for (int i = 0; i < count && fits; i++)
        {
            int o = i * 4;
            if (transparent && pixels[o + 3] < 128)
                continue;
            int color = pixels[o] | pixels[o + 1] << 8 | pixels[o + 2] << 16;
            if (!exact.ContainsKey(color))
            {
                if (exact.Count == maxColors)
                    fits = false;
                else
                    exact[color] = (byte)exact.Count;
            }
        }
        if (fits)
        {
            int transparentIndex = transparent ? exact.Count : -1;
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                indices[i] = transparent && pixels[o + 3] < 128
                    ? (byte)transparentIndex
                    : exact[pixels[o] | pixels[o + 1] << 8 | pixels[o + 2] << 16];
            }
            var palette = new byte[exact.Count * 3];
            foreach (var (color, index) in exact)
            {
                palette[index * 3] = (byte)color;
                palette[index * 3 + 1] = (byte)(color >> 8);
                palette[index * 3 + 2] = (byte)(color >> 16);
            }
            return (palette, indices, transparentIndex);
        }

        var wuPalette = new WuQuantizer(pixels, count, transparent).Build(maxColors);
        int transparentWu = transparent ? wuPalette.Length / 3 : -1;
        Dither(pixels, width, height, wuPalette, indices, transparent, transparentWu);
        return (wuPalette, indices, transparentWu);
    }

    /// <summary>Floyd–Steinberg, serpentine, with the nearest color looked up once per 6-bit cell of RGB space.</summary>
    private static void Dither(ReadOnlySpan<byte> pixels, int width, int height, byte[] palette, byte[] indices, bool transparent,
        int transparentIndex)
    {
        int colors = palette.Length / 3;
        var nearest = new short[1 << 18];
        Array.Fill(nearest, (short)-1);
        // The error carried to this row and the next, per channel, with a pixel of margin on each side.
        var current = new float[(width + 2) * 3];
        var next = new float[(width + 2) * 3];
        for (int y = 0; y < height; y++)
        {
            bool reverse = (y & 1) == 1;
            int step = reverse ? -1 : 1;
            for (int n = 0; n < width; n++)
            {
                int x = reverse ? width - 1 - n : n;
                int i = y * width + x;
                int o = i * 4;
                int e = (x + 1) * 3;
                if (transparent && pixels[o + 3] < 128)
                {
                    indices[i] = (byte)transparentIndex;
                    continue;
                }
                float b = Math.Clamp(pixels[o] + current[e], 0, 255);
                float g = Math.Clamp(pixels[o + 1] + current[e + 1], 0, 255);
                float r = Math.Clamp(pixels[o + 2] + current[e + 2], 0, 255);
                int key = ((int)r >> 2) << 12 | ((int)g >> 2) << 6 | (int)b >> 2;
                int index = nearest[key];
                if (index < 0)
                    nearest[key] = (short)(index = Nearest(palette, colors, r, g, b));
                indices[i] = (byte)index;

                float eb = b - palette[index * 3], eg = g - palette[index * 3 + 1], er = r - palette[index * 3 + 2];
                // 7/16 ahead, 3/16 behind below, 5/16 below, 1/16 ahead below.
                int ahead = e + step * 3, behind = e - step * 3;
                current[ahead] += eb * 7 / 16; current[ahead + 1] += eg * 7 / 16; current[ahead + 2] += er * 7 / 16;
                next[behind] += eb * 3 / 16; next[behind + 1] += eg * 3 / 16; next[behind + 2] += er * 3 / 16;
                next[e] += eb * 5 / 16; next[e + 1] += eg * 5 / 16; next[e + 2] += er * 5 / 16;
                next[ahead] += eb / 16; next[ahead + 1] += eg / 16; next[ahead + 2] += er / 16;
            }
            (current, next) = (next, current);
            Array.Clear(next);
        }
    }

    private static int Nearest(byte[] palette, int colors, float r, float g, float b)
    {
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int c = 0; c < colors; c++)
        {
            float db = b - palette[c * 3], dg = g - palette[c * 3 + 1], dr = r - palette[c * 3 + 2];
            float distance = dr * dr * 2 + dg * dg * 4 + db * db * 3;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = c;
            }
        }
        return best;
    }

    /// <summary>Wu's quantizer on a 33³ histogram of 5-bit colors with cumulative moments.</summary>
    private sealed class WuQuantizer
    {
        private const int Side = 33;
        private readonly double[] _weight = new double[Side * Side * Side];
        private readonly double[] _red = new double[Side * Side * Side];
        private readonly double[] _green = new double[Side * Side * Side];
        private readonly double[] _blue = new double[Side * Side * Side];
        private readonly double[] _squares = new double[Side * Side * Side];

        private struct Box
        {
            public int R0, R1, G0, G1, B0, B1;
            public readonly int Volume => (R1 - R0) * (G1 - G0) * (B1 - B0);
        }

        private enum Axis { Red, Green, Blue }

        private static int Index(int r, int g, int b) => r * Side * Side + g * Side + b;

        public WuQuantizer(ReadOnlySpan<byte> pixels, int count, bool transparent)
        {
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                if (transparent && pixels[o + 3] < 128)
                    continue;
                int b = pixels[o], g = pixels[o + 1], r = pixels[o + 2];
                int index = Index((r >> 3) + 1, (g >> 3) + 1, (b >> 3) + 1);
                _weight[index]++;
                _red[index] += r;
                _green[index] += g;
                _blue[index] += b;
                _squares[index] += r * r + g * g + b * b;
            }
            Accumulate();
        }

        /// <summary>Turns the histogram into moments summed from the origin, so any box's sums take eight lookups.</summary>
        private void Accumulate()
        {
            var area = new double[Side];
            var areaRed = new double[Side];
            var areaGreen = new double[Side];
            var areaBlue = new double[Side];
            var areaSquares = new double[Side];
            for (int r = 1; r < Side; r++)
            {
                Array.Clear(area); Array.Clear(areaRed); Array.Clear(areaGreen); Array.Clear(areaBlue); Array.Clear(areaSquares);
                for (int g = 1; g < Side; g++)
                {
                    double line = 0, lineRed = 0, lineGreen = 0, lineBlue = 0, lineSquares = 0;
                    for (int b = 1; b < Side; b++)
                    {
                        int index = Index(r, g, b);
                        line += _weight[index]; lineRed += _red[index]; lineGreen += _green[index]; lineBlue += _blue[index];
                        lineSquares += _squares[index];
                        area[b] += line; areaRed[b] += lineRed; areaGreen[b] += lineGreen; areaBlue[b] += lineBlue;
                        areaSquares[b] += lineSquares;
                        int below = Index(r - 1, g, b);
                        _weight[index] = _weight[below] + area[b];
                        _red[index] = _red[below] + areaRed[b];
                        _green[index] = _green[below] + areaGreen[b];
                        _blue[index] = _blue[below] + areaBlue[b];
                        _squares[index] = _squares[below] + areaSquares[b];
                    }
                }
            }
        }

        private static double Volume(in Box c, double[] m) =>
            m[Index(c.R1, c.G1, c.B1)] - m[Index(c.R1, c.G1, c.B0)] - m[Index(c.R1, c.G0, c.B1)] + m[Index(c.R1, c.G0, c.B0)]
            - m[Index(c.R0, c.G1, c.B1)] + m[Index(c.R0, c.G1, c.B0)] + m[Index(c.R0, c.G0, c.B1)] - m[Index(c.R0, c.G0, c.B0)];

        private static double Bottom(in Box c, Axis axis, double[] m) => axis switch
        {
            Axis.Red => -m[Index(c.R0, c.G1, c.B1)] + m[Index(c.R0, c.G1, c.B0)] + m[Index(c.R0, c.G0, c.B1)] - m[Index(c.R0, c.G0, c.B0)],
            Axis.Green => -m[Index(c.R1, c.G0, c.B1)] + m[Index(c.R1, c.G0, c.B0)] + m[Index(c.R0, c.G0, c.B1)] - m[Index(c.R0, c.G0, c.B0)],
            _ => -m[Index(c.R1, c.G1, c.B0)] + m[Index(c.R1, c.G0, c.B0)] + m[Index(c.R0, c.G1, c.B0)] - m[Index(c.R0, c.G0, c.B0)],
        };

        private static double Top(in Box c, Axis axis, int position, double[] m) => axis switch
        {
            Axis.Red => m[Index(position, c.G1, c.B1)] - m[Index(position, c.G1, c.B0)] - m[Index(position, c.G0, c.B1)] + m[Index(position, c.G0, c.B0)],
            Axis.Green => m[Index(c.R1, position, c.B1)] - m[Index(c.R1, position, c.B0)] - m[Index(c.R0, position, c.B1)] + m[Index(c.R0, position, c.B0)],
            _ => m[Index(c.R1, c.G1, position)] - m[Index(c.R1, c.G0, position)] - m[Index(c.R0, c.G1, position)] + m[Index(c.R0, c.G0, position)],
        };

        private double Variance(in Box c)
        {
            double r = Volume(c, _red), g = Volume(c, _green), b = Volume(c, _blue);
            return Volume(c, _squares) - (r * r + g * g + b * b) / Volume(c, _weight);
        }

        /// <summary>The best place to cut the box along an axis: the one that leaves the two halves with the least variance.</summary>
        private double Maximize(in Box c, Axis axis, int first, int last, out int cut, double wholeR, double wholeG, double wholeB,
            double wholeW)
        {
            double baseR = Bottom(c, axis, _red), baseG = Bottom(c, axis, _green), baseB = Bottom(c, axis, _blue);
            double baseW = Bottom(c, axis, _weight);
            double max = 0;
            cut = -1;
            for (int i = first; i < last; i++)
            {
                double halfR = baseR + Top(c, axis, i, _red), halfG = baseG + Top(c, axis, i, _green);
                double halfB = baseB + Top(c, axis, i, _blue), halfW = baseW + Top(c, axis, i, _weight);
                if (halfW == 0)
                    continue;
                double score = (halfR * halfR + halfG * halfG + halfB * halfB) / halfW;
                halfR = wholeR - halfR; halfG = wholeG - halfG; halfB = wholeB - halfB; halfW = wholeW - halfW;
                if (halfW == 0)
                    continue;
                score += (halfR * halfR + halfG * halfG + halfB * halfB) / halfW;
                if (score > max)
                {
                    max = score;
                    cut = i;
                }
            }
            return max;
        }

        private bool Cut(ref Box first, ref Box second)
        {
            double wholeR = Volume(first, _red), wholeG = Volume(first, _green), wholeB = Volume(first, _blue);
            double wholeW = Volume(first, _weight);
            double maxR = Maximize(first, Axis.Red, first.R0 + 1, first.R1, out int cutR, wholeR, wholeG, wholeB, wholeW);
            double maxG = Maximize(first, Axis.Green, first.G0 + 1, first.G1, out int cutG, wholeR, wholeG, wholeB, wholeW);
            double maxB = Maximize(first, Axis.Blue, first.B0 + 1, first.B1, out int cutB, wholeR, wholeG, wholeB, wholeW);
            Axis axis;
            if (maxR >= maxG && maxR >= maxB)
            {
                axis = Axis.Red;
                if (cutR < 0)
                    return false;
            }
            else
            {
                axis = maxG >= maxR && maxG >= maxB ? Axis.Green : Axis.Blue;
            }
            second.R1 = first.R1;
            second.G1 = first.G1;
            second.B1 = first.B1;
            switch (axis)
            {
                case Axis.Red:
                    second.R0 = first.R1 = cutR;
                    second.G0 = first.G0;
                    second.B0 = first.B0;
                    break;
                case Axis.Green:
                    second.G0 = first.G1 = cutG;
                    second.R0 = first.R0;
                    second.B0 = first.B0;
                    break;
                default:
                    second.B0 = first.B1 = cutB;
                    second.R0 = first.R0;
                    second.G0 = first.G0;
                    break;
            }
            return true;
        }

        /// <returns>BGR triples, at most <paramref name="maxColors"/>.</returns>
        public byte[] Build(int maxColors)
        {
            var boxes = new Box[maxColors];
            var variances = new double[maxColors];
            boxes[0] = new Box { R1 = Side - 1, G1 = Side - 1, B1 = Side - 1 };
            int count = maxColors;
            int next = 0;
            for (int i = 1; i < count; i++)
            {
                if (Cut(ref boxes[next], ref boxes[i]))
                {
                    variances[next] = boxes[next].Volume > 1 ? Variance(boxes[next]) : 0;
                    variances[i] = boxes[i].Volume > 1 ? Variance(boxes[i]) : 0;
                }
                else
                {
                    variances[next] = 0;
                    i--;
                }
                next = 0;
                double best = variances[0];
                for (int k = 1; k <= i; k++)
                {
                    if (variances[k] > best)
                    {
                        best = variances[k];
                        next = k;
                    }
                }
                if (best <= 0)
                {
                    count = i + 1;
                    break;
                }
            }

            var palette = new List<byte>(count * 3);
            for (int k = 0; k < count; k++)
            {
                double weight = Volume(boxes[k], _weight);
                if (weight <= 0)
                    continue;
                palette.Add((byte)Math.Clamp(Math.Round(Volume(boxes[k], _blue) / weight), 0, 255));
                palette.Add((byte)Math.Clamp(Math.Round(Volume(boxes[k], _green) / weight), 0, 255));
                palette.Add((byte)Math.Clamp(Math.Round(Volume(boxes[k], _red) / weight), 0, 255));
            }
            return palette.Count > 0 ? [.. palette] : [0, 0, 0];
        }
    }
}
