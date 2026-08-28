using System.Net;
using Microsoft.Extensions.Options;
using Xunit;

namespace Shoko.AniListScrobble.Tests;

public sealed class ArmCatalogTests
{
    [Theory]
    [InlineData("{\"anilist\":20958}", 20958)]
    [InlineData("{\"anilist\":290,\"anidb\":1}", 290)]
    [InlineData("null", null)]
    [InlineData("{}", null)]
    [InlineData("{\"anilist\":0}", null)]
    [InlineData("", null)]
    public void ParseRelationReadsAnilistIdOrMiss(string json, int? expected)
        => Assert.Equal(expected, ArmCatalog.ParseRelation(json));

    [Fact]
    public void ParseRelationReadsFixture()
    {
        var json = File.ReadAllText(Path.Combine(FindFixtures(), "arm.json"));
        Assert.Equal(20958, ArmCatalog.ParseRelation(json));
    }

    [Fact]
    public void IdsPathQueriesAnidbWithAnilistInclude()
        => Assert.Equal("/api/v2/ids?source=anidb&id=10944&include=anilist", ArmCatalog.IdsPath("anidb", 10944));

    [Fact]
    public void InMemoryCatalogPrefersAnidbOverMal()
    {
        var arm = new InMemoryArmCatalog
        {
            AnidbMap = { [10944] = 20958 },
            Map = { [5114] = 5114 },
        };
        Assert.True(arm.TryResolveAnidb(10944, out var anilistId));
        Assert.Equal(20958, anilistId);
        Assert.True(arm.TryResolveMal([999, 5114], out var malId));
        Assert.Equal(5114, malId);
        Assert.False(arm.TryResolveAnidb(1, out _));
        Assert.False(arm.TryResolveMal([123], out _));
    }

    [Fact]
    public async Task WarmLooksUpAnidbAndCachesTheResult()
    {
        using var handle = CreateCatalog(out var handler, out var cachePath);
        handler.Responses.Enqueue(RecordingHandler.JsonResponse(HttpStatusCode.OK, """{"anilist":20958}"""));

        await handle.Catalog.WarmAsync(10944, [5114], CancellationToken.None);

        Assert.True(handle.Catalog.TryResolveAnidb(10944, out var anilistId));
        Assert.Equal(20958, anilistId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v2/ids?source=anidb&id=10944&include=anilist", request.Uri.PathAndQuery);
        Assert.True(File.Exists(cachePath));

        await handle.Catalog.WarmAsync(10944, [5114], CancellationToken.None);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WarmFallsBackToMalWhenAnidbReturnsNull()
    {
        using var handle = CreateCatalog(out var handler, out _);
        handler.Responses.Enqueue(RecordingHandler.JsonResponse(HttpStatusCode.OK, "null"));
        handler.Responses.Enqueue(RecordingHandler.JsonResponse(HttpStatusCode.OK, """{"anilist":5114}"""));

        await handle.Catalog.WarmAsync(10944, [5114], CancellationToken.None);

        Assert.False(handle.Catalog.TryResolveAnidb(10944, out _));
        Assert.True(handle.Catalog.TryResolveMal([5114], out var anilistId));
        Assert.Equal(5114, anilistId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/v2/ids?source=anidb&id=10944&include=anilist", handler.Requests[0].Uri.PathAndQuery);
        Assert.Equal("/api/v2/ids?source=myanimelist&id=5114&include=anilist", handler.Requests[1].Uri.PathAndQuery);
    }

    [Fact]
    public async Task WarmDoesNotCallMalWhenAnidbHits()
    {
        using var handle = CreateCatalog(out var handler, out _);
        handler.Responses.Enqueue(RecordingHandler.JsonResponse(HttpStatusCode.OK, """{"anilist":20958}"""));

        await handle.Catalog.WarmAsync(10944, [5114], CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.False(handle.Catalog.TryResolveMal([5114], out _));
    }

    [Fact]
    public async Task StaleNegativeCacheIsRefetched()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"arm-{Guid.NewGuid():N}.json");
        var stale = new
        {
            anidb = new Dictionary<string, object>
            {
                ["10944"] = new { anilistId = (int?)null, cachedAt = DateTimeOffset.UtcNow.AddDays(-8) },
            },
            mal = new Dictionary<string, object>(),
        };
        await File.WriteAllTextAsync(cachePath, System.Text.Json.JsonSerializer.Serialize(stale, ArmCatalog.JsonOptions));
        try
        {
            using var handle = CreateCatalog(out var handler, out _, cachePath);
            handler.Responses.Enqueue(RecordingHandler.JsonResponse(HttpStatusCode.OK, """{"anilist":20958}"""));

            await handle.Catalog.WarmAsync(10944, [], CancellationToken.None);

            Assert.True(handle.Catalog.TryResolveAnidb(10944, out var anilistId));
            Assert.Equal(20958, anilistId);
            Assert.Single(handler.Requests);
        }
        finally
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
    }

    private static ArmCatalogHandle CreateCatalog(out RecordingHandler handler, out string cachePath, string? existingPath = null)
    {
        handler = new RecordingHandler();
        cachePath = existingPath ?? Path.Combine(Path.GetTempPath(), $"arm-{Guid.NewGuid():N}.json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://arm.haglund.dev") };
        var options = Options.Create(new AniListScrobbleOptions { ArmCacheHours = 168, RequestTimeoutSeconds = 20 });
        var catalog = new ArmCatalog(http, cachePath, options, new RecordingLogger<ArmCatalog>());
        return new ArmCatalogHandle(catalog, http, cachePath, existingPath is null);
    }

    private static string FindFixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "fixtures", "arm.json");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate)!;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("fixtures/arm.json was not found.");
    }

    private sealed class ArmCatalogHandle : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _cachePath;
        private readonly bool _deleteCache;

        public ArmCatalogHandle(ArmCatalog catalog, HttpClient http, string cachePath, bool deleteCache)
        {
            Catalog = catalog;
            _http = http;
            _cachePath = cachePath;
            _deleteCache = deleteCache;
        }

        public ArmCatalog Catalog { get; }

        public void Dispose()
        {
            _http.Dispose();
            if (_deleteCache && File.Exists(_cachePath))
                File.Delete(_cachePath);
        }
    }
}
