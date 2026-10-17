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
            SingleInstance.Attach((paths, arrivedAt, _) =>
            {
                received.Enqueue((paths, arrivedAt));
                return Task.FromResult(true);
            });
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

            // A window that can't take them (its dispatcher is shutting down) doesn't answer, and the sender opens its own.
            SingleInstance.Attach((_, _, _) => Task.FromResult(false));
            Assert.False(SingleInstance.TryHandOver(["refused.png"]));

            // The window says only once it has taken them, which may take a moment on a busy UI thread.
            SingleInstance.Attach(async (_, _, _) =>
            {
                await Task.Delay(300);
                return true;
            });
            Assert.True(SingleInstance.TryHandOver(["slow.png"]));

            // Nor does a window that is closing answer, and the sender doesn't wait for the timeout to find out.
            SingleInstance.Attach((_, _, _) => Task.FromResult(true));
            SingleInstance.Stop();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(SingleInstance.TryHandOver(["late.png"]));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
        }
        finally
        {
            SingleInstance.Release();
        }
    }
}
