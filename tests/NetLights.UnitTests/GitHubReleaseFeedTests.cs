using System.Net;
using System.Net.Http;
using NetLights.Updates;
using Xunit;

namespace NetLights.UnitTests;

public sealed class GitHubReleaseFeedTests
{
    [Fact]
    public async Task IsReachable_SendsHttp11HeadToGitHubOrigin()
    {
        RecordingHandler handler = new(HttpStatusCode.OK);
        using GitHubReleaseFeed feed = new(handler, timeout: TimeSpan.FromSeconds(5), githubApi: false);
        Assert.True(await feed.IsReachable(CancellationToken.None));
        Assert.Equal(HttpMethod.Head, handler.Method);
        Assert.Equal(new Uri("https://github.com/"), handler.Uri);
        Assert.Equal(HttpVersion.Version11, handler.Version);
    }

    [Fact]
    public async Task IsReachable_TreatsRedirectStatusAsOnline()
    {
        using GitHubReleaseFeed feed = new(new RecordingHandler(HttpStatusCode.Found), githubApi: false);
        Assert.True(await feed.IsReachable(CancellationToken.None));
    }

    [Fact]
    public async Task IsReachable_TimeoutIsOffline()
    {
        using GitHubReleaseFeed feed = new(new HangHandler(), timeout: TimeSpan.FromMilliseconds(50), githubApi: false);
        Assert.False(await feed.IsReachable(CancellationToken.None));
    }

    [Fact]
    public void ProbeUrl_IsGitHubOriginHeadNotHtmlRepoPage()
        => Assert.Equal("https://github.com/", SilentUpdatePolicy.ProbeUrl);

    [Fact]
    public void ProbeTimeout_MatchesQueryBudget()
        => Assert.Equal(GitHubReleaseFeed.QueryTimeout, NetworkWaitPolicy.ProbeTimeout);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public RecordingHandler(HttpStatusCode status) => _status = status;

        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public Version? Version { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Version = request.Version;
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }

    private sealed class HangHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
