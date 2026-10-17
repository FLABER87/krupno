using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

public class PackTests
{
    private static readonly float[] Special =
    [
        0f, 1f, 0.5f, -0f, -0.001f, 1.001f, -1f, 2f, 255f, -255f, 1e9f, -1e9f, float.MaxValue, float.MinValue,
        float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.Epsilon, 0.5f / 255f, 1.5f / 255f,
        254.5f / 255f, BitConverter.Int32BitsToSingle(0x7FC00001), // another NaN
    ];

    [Fact]
    public void MatchesScalarConversion()
    {
        // Lengths below, at and past a vector, so both the SIMD loop and the scalar tail run.
        var random = new Random(5);
        foreach (int length in new[] { 1, 3, 4, 7, 8, 9, 15, 16, 17, 31, 33, 64, 100, 1000 })
        {
            var r = new float[length];
            var g = new float[length];
            var b = new float[length];
            for (int x = 0; x < length; x++)
            {
                r[x] = Pick(random);
                g[x] = Pick(random);
                b[x] = Pick(random);
            }

            var packed = new uint[length];
            TileRunner.Pack(r, g, b, packed);
            for (int x = 0; x < length; x++)
            {
                uint expected = TileRunner.ToByte(b[x]) | (uint)TileRunner.ToByte(g[x]) << 8 | (uint)TileRunner.ToByte(r[x]) << 16 | 0xFF000000;
                Assert.True(expected == packed[x], $"length {length}, x {x}: r {r[x]}, g {g[x]}, b {b[x]} → {packed[x]:X8}, expected {expected:X8}");
            }
        }
    }

    [Fact]
    public void EveryByteLevelRoundTrips()
    {
        var levels = Enumerable.Range(0, 256).Select(v => v / 255f).ToArray();
        var packed = new uint[256];
        TileRunner.Pack(levels, levels, levels, packed);
        for (int v = 0; v < 256; v++)
            Assert.Equal(0xFF000000 | (uint)(v * 0x010101), packed[v]);
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        float[] values = [-1f, 2f, float.NegativeInfinity, float.PositiveInfinity, float.NaN];
        var packed = new uint[values.Length];
        TileRunner.Pack(values, values, values, packed);
        Assert.Equal([0xFF000000, 0xFFFFFFFF, 0xFF000000, 0xFFFFFFFF, 0xFF000000], packed);
    }

    private static float Pick(Random random) => random.Next(3) switch
    {
        0 => Special[random.Next(Special.Length)],
        1 => (float)random.NextDouble(),
        _ => (float)(random.NextDouble() * 4 - 2),
    };
}
