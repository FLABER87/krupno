using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Krupno.App.Infrastructure;
using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

/// <summary>
/// "Update" (src/Krupno.App/Infrastructure/UpdateInstall.cs, compiled into the tests): github.com's release downloads are played
/// by a handler, and the installer by a few bytes whose SHA-256 SHA256SUMS.txt gives or doesn't.
/// </summary>
public class UpdateInstallTests : IDisposable
{
    private const string Download = "https://github.com/FLABER87/krupno/releases/download/v0.3.0/";
    private const string Installer = "Krupno-0.3.0-setup-x64.exe";
    private static readonly Version Version = new(0, 3, 0);
    private static readonly byte[] Setup = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("MZ not really a setup ", 20_000)));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"krupno-update-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    /// <summary>The release's files by name; anything else is a 404.</summary>
    private sealed class Release(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            string uri = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(uri.StartsWith(Download, StringComparison.Ordinal) && files.TryGetValue(uri[Download.Length..], out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Recorder : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>SHA256SUMS.txt as installer/build.ps1 writes it: "hash *name" per line.</summary>
    private static byte[] Sums(string installerHash) => Encoding.ASCII.GetBytes(
        $"{installerHash} *{Installer}\n{new string('0', 64)} *Krupno-0.3.0-win-x64.zip\n");

    [Fact]
    public async Task DownloadsTheInstallerWhoseHashMatches()
    {
        var github = new Release(new() { ["SHA256SUMS.txt"] = Sums(Sha256(Setup)), [Installer] = Setup });
        var progress = new Recorder();
        var installer = await UpdateInstall.DownloadAsync(Version, progress, CancellationToken.None, github, _folder);

        Assert.Equal(Path.Combine(_folder, Installer), installer.Path);
        Assert.Equal(Sha256(Setup), installer.Sha256);
        Assert.Equal(Setup, File.ReadAllBytes(installer.Path));
        Assert.Equal([Installer], Directory.GetFiles(_folder).Select(Path.GetFileName));
        Assert.Equal(1, progress.Values[^1]);
        Assert.True(progress.Values.Count > 1, "progress is reported along the way");
        // The direct links of the release, no API.
        Assert.Equal([Download + "SHA256SUMS.txt", Download + Installer], github.Requests.Select(r => r.RequestUri!.AbsoluteUri));
        Assert.All(github.Requests, r => Assert.StartsWith("Krupno/", r.Headers.UserAgent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInstallerThatDoesNotMatchIsDeleted()
    {
        var github = new Release(new() { ["SHA256SUMS.txt"] = Sums(new string('a', 64)), [Installer] = Setup });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateInstall.DownloadAsync(Version, new Recorder(), CancellationToken.None, github, _folder));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task NoLineForTheInstallerIsNoDownload()
    {
        var github = new Release(new() { ["SHA256SUMS.txt"] = "nothing here\n"u8.ToArray(), [Installer] = Setup });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateInstall.DownloadAsync(Version, new Recorder(), CancellationToken.None, github, _folder));
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task AMissingFileIsAnHttpError()
    {
        var github = new Release(new() { ["SHA256SUMS.txt"] = Sums(Sha256(Setup)) });
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            UpdateInstall.DownloadAsync(Version, new Recorder(), CancellationToken.None, github, _folder));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task AnInstallerAlreadyDownloadedIsKept()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, Installer), Setup);
        var github = new Release(new() { ["SHA256SUMS.txt"] = Sums(Sha256(Setup)) });
        var installer = await UpdateInstall.DownloadAsync(Version, new Recorder(), CancellationToken.None, github, _folder);
        Assert.Equal(Setup, File.ReadAllBytes(installer.Path));
        Assert.Single(github.Requests);
    }

    [Fact]
    public void AnInstallerChangedAfterTheDownloadIsNotStarted()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, Installer);
        File.WriteAllBytes(path, Setup);
        var verified = new UpdateInstall.Verified(path, Sha256(Setup));
        File.AppendAllText(path, "and something else");
        Assert.Throws<InvalidDataException>(() => UpdateInstall.Start(verified, false, "en", Version));
    }

    [Fact]
    public async Task CanceledIsCanceled()
    {
        var github = new Release(new() { ["SHA256SUMS.txt"] = Sums(Sha256(Setup)), [Installer] = Setup });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdateInstall.DownloadAsync(Version, new Recorder(), cts.Token, github, _folder));
    }

    [Theory]
    // As build.ps1 writes it (sha256sum's binary mode), and as sha256sum writes text files.
    [InlineData("a8e6cf350990d7cd7879abb6fcb140a0a4e98733c37a9cccdd000454cac0aadc *Krupno-0.2.1-setup-x64.exe\n", "a8e6cf350990d7cd7879abb6fcb140a0a4e98733c37a9cccdd000454cac0aadc")]
    [InlineData("A8E6CF350990D7CD7879ABB6FCB140A0A4E98733C37A9CCCDD000454CAC0AADC  Krupno-0.2.1-setup-x64.exe\r\n", "a8e6cf350990d7cd7879abb6fcb140a0a4e98733c37a9cccdd000454cac0aadc")]
    [InlineData("a8e6cf350990d7cd7879abb6fcb140a0a4e98733c37a9cccdd000454cac0aadc *Krupno-0.2.1-setup-x64.exe.sig\n", null)]
    [InlineData("a8e6cf35 *Krupno-0.2.1-setup-x64.exe\n", null)]
    [InlineData("", null)]
    public void HashOfTheInstallerFromTheList(string sums, string? hash) =>
        Assert.Equal(hash, UpdateInstall.FindHash("ba02ac875e47789c0535973593f3bfec1328480359bf00a198bb08d462f33c15 *Krupno-0.2.1-win-x64.zip\n" + sums,
            "Krupno-0.2.1-setup-x64.exe"));

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Krupno\", @"C:\Users\u\AppData\Local\Programs\Krupno\", null, false)]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Krupno\", @"c:\users\u\appdata\local\programs\krupno", null, false)]
    [InlineData(@"C:\Program Files\Krupno\", @"C:\Users\u\AppData\Local\Programs\Krupno\", @"C:\Program Files\Krupno\", true)]
    [InlineData(@"E:\Projects\Krupno\bin\Release\", @"C:\Users\u\AppData\Local\Programs\Krupno\", @"C:\Program Files\Krupno\", null)]
    [InlineData(@"D:\Krupno-portable\", null, "", null)]
    public void OnlyTheInstalledFolderUpdatesItself(string appFolder, string? currentUser, string? allUsers, bool? forAllUsers) =>
        Assert.Equal(forAllUsers, UpdateInstall.InstallMode(appFolder, currentUser, allUsers));

    [Fact]
    public void SetupCommandLine()
    {
        Assert.Equal(["/SILENT", "/NORESTART", "/CLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", "/CURRENTUSER", "/LANG=ptbr",
                @"/LOG=C:\logs\setup-0.3.0.log", "/UPDATE=1"],
            UpdateInstall.Arguments(false, "pt-BR", @"C:\logs\setup-0.3.0.log"));
        Assert.Contains("/ALLUSERS", UpdateInstall.Arguments(true, "en", "log"));

        // Every language of the window is one of the installer's, by the name /LANG takes.
        string script = File.ReadAllText(Path.Combine(Root(), "installer", "Krupno.iss"));
        var languages = Regex.Matches(script, @"^Name: ""(\w+)""; MessagesFile:", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.All(UiLanguage.All, l => Assert.Contains(UpdateInstall.Arguments(false, l.Culture, "log")[5]["/LANG=".Length..], languages));
        // Setup knows /UPDATE=1, waits for every Krupno by the mutex they hold, and holds its own while it installs.
        Assert.Contains("{param:UPDATE|0}", script);
        Assert.Matches(new Regex($@"(?m)^AppMutex={Regex.Escape(RunningMarker.DefaultName)}\r?$"), script);
        Assert.Matches(new Regex($@"(?m)^SetupMutex={Regex.Escape(RunningMarker.DefaultSetupName)}\r?$"), script);
    }

    [Theory]
    // Setup took over at once, after a while (a UAC prompt), or just as its process ended.
    [InlineData(new[] { true }, new bool[0], true)]
    [InlineData(new[] { false, false, true }, new[] { false, false }, true)]
    [InlineData(new[] { false, true }, new[] { true }, true)]
    // It ended before taking over: the UAC prompt declined, "Krupno is running" canceled.
    [InlineData(new[] { false, false }, new[] { true }, false)]
    public async Task TheWindowClosesOnceSetupHasTakenOver(bool[] running, bool[] ended, bool tookOver)
    {
        int r = 0, e = 0;
        Assert.Equal(tookOver, await UpdateInstall.WaitForSetupAsync(() => running[Math.Min(r++, running.Length - 1)],
            () => ended[Math.Min(e++, ended.Length - 1)], TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void SetupIsSeenByItsMutex()
    {
        RunningMarker.SetupName = $"Krupno-test-setup-{Guid.NewGuid():N}";
        try
        {
            Assert.False(RunningMarker.SetupRunning());
            using (new Mutex(false, RunningMarker.SetupName))
                Assert.True(RunningMarker.SetupRunning());
            Assert.False(RunningMarker.SetupRunning());
        }
        finally
        {
            RunningMarker.SetupName = RunningMarker.DefaultSetupName;
        }
    }

    [Fact]
    public void AnyoneMayWaitOnTheRunningMarker()
    {
        // Setup may run as another user or elevated, and only waits on it (OpenMutex with SYNCHRONIZE).
        RunningMarker.Name = $"Krupno-test-{Guid.NewGuid():N}";
        try
        {
            RunningMarker.Hold();
            using var marker = MutexAcl.OpenExisting(RunningMarker.Name, MutexRights.ReadPermissions);
            var rules = marker.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<MutexAccessRule>();
            Assert.Contains(rules, rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.WorldSid, null))
                                           && rule.AccessControlType == AccessControlType.Allow
                                           && rule.MutexRights.HasFlag(MutexRights.Synchronize));
        }
        finally
        {
            RunningMarker.Release();
            RunningMarker.Name = RunningMarker.DefaultName;
        }
    }

    [Fact]
    public void OtherKrupnosAreSeenByTheirMutex()
    {
        RunningMarker.Name = $"Krupno-test-{Guid.NewGuid():N}";
        try
        {
            Assert.False(RunningMarker.OthersRunning());
            RunningMarker.Hold();
            // This process's own doesn't count.
            Assert.False(RunningMarker.OthersRunning());
            using (new Mutex(false, RunningMarker.Name))
                Assert.True(RunningMarker.OthersRunning());
            Assert.False(RunningMarker.OthersRunning());
            // Still held after checking: Setup must see this one.
            Assert.True(Mutex.TryOpenExisting(RunningMarker.Name, out var held));
            held.Dispose();
            RunningMarker.Release();
            Assert.False(Mutex.TryOpenExisting(RunningMarker.Name, out _));
        }
        finally
        {
            RunningMarker.Release();
            RunningMarker.Name = RunningMarker.DefaultName;
        }
    }

    /// <summary>The repository this file was compiled from: the folder above it that holds Krupno.slnx.</summary>
    private static string Root([CallerFilePath] string source = "")
    {
        for (var dir = new FileInfo(source).Directory; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Krupno.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"The repository root (Krupno.slnx) was not found above {source}.");
    }
}
