using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

public class TilePlannerTests
{
    [Fact]
    public void AxisWithinOneWindowIsOneSpan()
    {
        Assert.Equal([new TileSpan(0, 300, 0)], TilePlanner.PlanAxis(300, 384, 32));
        Assert.Equal([new TileSpan(0, 384, 0)], TilePlanner.PlanAxis(384, 384, 32));
    }

    [Fact]
    public void KnownPlan()
    {
        // 1920 px, window 384, context 32: the cores advance by 320 and the last window is pushed against the edge.
        Assert.Equal(
            [
                new TileSpan(0, 352, 0), new TileSpan(352, 320, 320), new TileSpan(672, 320, 640),
                new TileSpan(992, 320, 960), new TileSpan(1312, 320, 1280), new TileSpan(1632, 288, 1536),
            ],
            TilePlanner.PlanAxis(1920, 384, 32));
    }

    [Theory]
    [InlineData(1920, 384, 32)]
    [InlineData(1200, 384, 64)]
    [InlineData(385, 384, 0)]
    [InlineData(1000, 64, 16)]
    [InlineData(1001, 64, 31)]
    [InlineData(7, 3, 1)]
    [InlineData(100000, 512, 32)]
    public void CoresCoverTheAxisWithRealContext(int length, int window, int padding)
    {
        var spans = TilePlanner.PlanAxis(length, window, padding);
        int next = 0;
        foreach (var span in spans)
        {
            // Cores follow each other without gaps or overlaps.
            Assert.Equal(next, span.CoreStart);
            Assert.True(span.CoreLength > 0);
            next = span.CoreStart + span.CoreLength;

            // The window stays inside the image and holds the core with the context around it, except at the image edges.
            Assert.InRange(span.WindowStart, 0, length - window);
            int before = span.CoreStart - span.WindowStart;
            int after = span.WindowStart + window - next;
            Assert.True(before >= padding || span.WindowStart == 0, $"{span}: {before} px of context before the core");
            Assert.True(after >= padding || span.WindowStart + window == length, $"{span}: {after} px of context after the core");
            Assert.True(after >= 0);
        }
        Assert.Equal(length, next);
    }

    [Fact]
    public void WindowMustExceedTwiceThePadding()
    {
        Assert.Throws<ArgumentException>(() => TilePlanner.PlanAxis(1000, 64, 32));
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(4, 5, 4)]
    [InlineData(-1, 5, 1)]
    [InlineData(-4, 5, 4)]
    [InlineData(-5, 5, 3)]
    [InlineData(5, 5, 3)]
    [InlineData(8, 5, 0)]
    [InlineData(9, 5, 1)]
    [InlineData(-100, 1, 0)]
    [InlineData(7, 1, 0)]
    [InlineData(-1, 2, 1)]
    [InlineData(2, 2, 0)]
    public void MirrorKnownValues(int index, int length, int expected)
    {
        Assert.Equal(expected, TilePlanner.Mirror(index, length));
    }

    [Fact]
    public void MirrorMatchesStepByStepReflection()
    {
        for (int length = 1; length <= 9; length++)
        {
            for (int index = -40; index <= 40; index++)
                Assert.Equal(Reflect(index, length), TilePlanner.Mirror(index, length));
        }

        // Bounce off the edges one step at a time: ... 2 1 [0 1 2 3] 2 1 0 1 ...
        static int Reflect(int index, int length)
        {
            if (length == 1)
                return 0;
            int position = 0, direction = index < 0 ? -1 : 1;
            for (int step = 0; step < Math.Abs(index); step++)
            {
                if (position + direction is < 0 || position + direction >= length)
                    direction = -direction;
                position += direction;
            }
            return position;
        }
    }
}
