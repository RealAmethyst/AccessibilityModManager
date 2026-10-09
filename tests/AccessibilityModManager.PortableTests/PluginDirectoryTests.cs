using System.Net;
using System.Net.Http.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class PluginDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-directory-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task New_games_are_announced_once_across_restarts_and_sources_have_separate_baselines()
    {
        var config = new ConfigService(logger, root);
        var observer = new NewGameNotifications(config);
        var first = new ObservedPluginGames("plugin|https://example.com/index.json", "Amethyst",
            [new() { GameId = "old", DisplayName = "Old Game" }]);
        Assert.Empty(await observer.ObserveAsync([first]));
        var updated = first with { Games = [.. first.Games, new() { GameId = "new", DisplayName = "New Game" }] };
        Assert.Equal(["Amethyst just added New Game."], await observer.ObserveAsync([updated]));
        Assert.Empty(await new NewGameNotifications(new ConfigService(logger, root)).ObserveAsync([updated]));
        Assert.Empty(await observer.ObserveAsync([first])); // Removing and re-adding an ID is not a new game.
        Assert.Empty(await observer.ObserveAsync([updated]));
        Assert.Empty(await observer.ObserveAsync([updated with { SourceKey = "another source" }]));
        Assert.Equal(2, (await config.LoadAsync()).KnownPluginGameIds.Count);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task Unavailable_or_unreachable_service_refuses_downloads(bool available, HttpStatusCode status)
    {
        using var http = new HttpClient(new Reply(status, new PluginAvailability(available)));
        var client = new PluginDirectoryClient(http);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequireAvailableAsync(
            "https://raw.githubusercontent.com/author/repo/main/index.json"));
        Assert.Contains("installed files have not been changed", error.Message);
    }

    [Fact]
    public async Task Directory_approval_does_not_replace_explicit_source_consent_or_official_catalog_trust()
    {
        using var http = new HttpClient(new Reply(HttpStatusCode.OK, new PluginAvailability(true)));
        await new PluginDirectoryClient(http).RequireAvailableAsync("https://raw.githubusercontent.com/author/repo/main/index.json");
        var config = new ConfigService(logger, root);
        Assert.Empty((await config.LoadAsync()).UserPluginSources);
        using var offline = new HttpClient(new Offline());
        await new PluginDirectoryClient(offline).RequireAvailableAsync("https://accessibilitymods.com/registry/plugins/amethyst/index.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PluginDirectoryClient(offline)
            .RequireAvailableAsync("https://raw.githubusercontent.com/author/repo/main/index.json"));
    }

    [Fact]
    public async Task Registration_posts_only_public_repository_metadata()
    {
        var handler = new Reply(HttpStatusCode.Accepted, new { status = "received" });
        using var http = new HttpClient(handler);
        await new PluginDirectoryClient(http).SubmitAsync(new("author/catalog", "index.json", "plugin"));
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(PluginDirectoryProtocol.Url + "/register", handler.Address);
        Assert.Contains("author/catalog", handler.Body);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task Directory_rejects_sources_outside_public_GitHub_indexes()
    {
        var entry = new PluginDirectoryEntry("id", "plugin", "Author", "author", 42, "author/repo", "https://evil.example/index.json", []);
        using var http = new HttpClient(new Reply(HttpStatusCode.OK, new PluginDirectorySnapshot(1, [entry])));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PluginDirectoryClient(http).ListAsync());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); (logger as IDisposable)?.Dispose(); }
    private sealed class Reply(HttpStatusCode status, object body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Address { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method; Address = request.RequestUri!.AbsoluteUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Authorization = request.Headers.Authorization?.ToString();
            return new(status) { Content = JsonContent.Create(body) };
        }
    }
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("Offline test");
    }
}
