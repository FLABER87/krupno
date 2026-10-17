using System.Collections.Concurrent;
using Krupno.App.Infrastructure;

namespace Krupno.Core.Tests;

/// <summary>
/// The hand-over between Krupno.exe processes (src/Krupno.App/Infrastructure/SingleInstance.cs), in one process: the test
/// becomes the window that takes the paths, and the "processes" that hand theirs over are threads.
/// </summary>
public class SingleInstanceTests
{
    [Fact]
    public void ConcurrentHandOversAllArrive()
    {
        // A name of its own, so a Krupno window that is open doesn't take these paths.
        SingleInstance.Name = $"Krupno-test-{Guid.NewGuid():N}";
        Assert.False(SingleInstance.TryHandOver([]), "The first one should become the window.");
        try
        {
            var received = new ConcurrentQueue<(string[] Paths, DateTime ArrivedAt)>();

            // Handed over while the window is still starting: kept until it attaches.
            Assert.True(SingleInstance.TryHandOver(["early.png"]));
            SingleInstance.Attach((paths, arrivedAt) => received.Enqueue((paths, arrivedAt)));
            Assert.Equal([Path.GetFullPath("early.png")], Assert.Single(received).Paths);

            // Explorer starts a process for every file of a selection, all at once.
            const int Clients = 12;
            var handedOver = new bool[Clients];
            var threads = Enumerable.Range(0, Clients)
                .Select(i => new Thread(() => handedOver[i] = SingleInstance.TryHandOver([$"file{i}.png", $"other{i}.jpg"])))
                .ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.All(handedOver, h => Assert.True(h));
            // The window answers only after the handler has the paths, so all of them are in.
            var lists = received.Skip(1).Select(r => r.Paths).ToList();
            Assert.Equal(Clients, lists.Count);
            Assert.Equal(
                Enumerable.Range(0, Clients).Select(i => $"{Path.GetFullPath($"file{i}.png")}|{Path.GetFullPath($"other{i}.jpg")}").Order(),
                lists.Select(l => string.Join("|", l)).Order());
        }
        finally
        {
            SingleInstance.Release();
        }
    }
}
