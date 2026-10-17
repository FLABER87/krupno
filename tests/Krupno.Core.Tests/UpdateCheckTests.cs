using System.Net;
using System.Net.Http;
using Krupno.App.Infrastructure;

namespace Krupno.Core.Tests;

/// <summary>
/// The update check (src/Krupno.App/Infrastructure/UpdateCheck.cs, compiled into the tests): github.com is played by a handler
/// that answers the way github.com/…/releases/latest does, with a redirect to the latest release's tag.
/// </summary>
public class UpdateCheckTests
{
    private sealed class Redirect(HttpStatusCode status, string? location) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(status);
            if (location is not null)
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return Task.FromResult(response);
        }
    }

    private const string Release = "https://github.com/FLABER87/krupno/releases/tag/";

    [Theory]
    [InlineData(Release + "v0.3.0", "0.3.0")]
    [InlineData(Release + "v1.12.105/", "1.12.105")]
    [InlineData(Release + "models-v2", null)]
    [InlineData(Release + "v1.2", null)]
    [InlineData(Release + "v1.2.3-beta", null)]
    [InlineData("https://github.com/FLABER87/krupno/releases", null)]
    public void TagsOfKrupnoReleasesOnly(string page, string? version) =>
        Assert.Equal(version, UpdateCheck.ParseTag(new Uri(page))?.ToString());

    [Fact]
    public async Task NewerReleaseIsFound()
    {
        var github = new Redirect(HttpStatusCode.Found, Release + "v0.3.0");
        var newer = await UpdateCheck.FindNewerAsync(new Version(0, 2, 0), github);
        Assert.Equal((new Version(0, 3, 0), Release + "v0.3.0"), newer);

        // One HEAD request to the page, not the API: the redirect itself is the answer, it isn't followed.
        var request = Assert.Single(github.Requests);
        Assert.Equal(HttpMethod.Head, request.Method);
        Assert.Equal(UpdateCheck.LatestUrl, request.RequestUri!.AbsoluteUri);
        Assert.Contains("Krupno/0.2.0", request.Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, Release + "v0.2.0")]
    [InlineData(HttpStatusCode.Found, Release + "v0.1.9")]
    [InlineData(HttpStatusCode.Found, Release + "models-v3")]
    [InlineData(HttpStatusCode.OK, null)]
    [InlineData(HttpStatusCode.NotFound, null)]
    public async Task NothingNewer(HttpStatusCode status, string? location) =>
        Assert.Null(await UpdateCheck.FindNewerAsync(new Version(0, 2, 0), new Redirect(status, location)));

    [Fact]
    public async Task RelativeRedirectIsResolved()
    {
        var newer = await UpdateCheck.FindNewerAsync(new Version(0, 2, 0), new Redirect(HttpStatusCode.Found, "/FLABER87/krupno/releases/tag/v1.0.0"));
        Assert.Equal((new Version(1, 0, 0), Release + "v1.0.0"), newer);
    }

    [Fact]
    public void InstallerAndUserChoices()
    {
        bool Decide(bool? choice, string? setAt, (bool, string)? installer, out bool? newChoice, out string? newSetAt)
        {
            bool check = UpdateCheck.Decide(ref choice, ref setAt, installer);
            (newChoice, newSetAt) = (choice, setAt);
            return check;
        }

        // No installer (the portable ZIP): on, unless the user turned it off.
        Assert.True(Decide(null, null, null, out _, out _));
        Assert.False(Decide(false, null, null, out _, out _));
        // The installer's choice, until the user makes one.
        Assert.True(Decide(null, null, (true, "A"), out _, out _));
        Assert.True(Decide(true, "A", (true, "A"), out _, out _));
        Assert.False(Decide(false, "A", (true, "B"), out var kept, out _));
        Assert.False(kept);
        // Turned off in a new installation: off, whatever the user chose before, and the choice is dropped.
        Assert.False(Decide(true, "A", (false, "B"), out var dropped, out var at));
        Assert.Null(dropped);
        Assert.Equal("B", at);
        // Turned on in the window after that installation: the user's choice stands.
        Assert.True(Decide(true, "B", (false, "B"), out var stands, out _));
        Assert.True(stands);
    }
}
