using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shoko.Abstractions.Plugin;

namespace Shoko.AniListScrobble;

public interface IArmCatalog
{
    bool TryResolveAnidb(int anidbId, out int anilistId);
    bool TryResolveMal(IReadOnlyList<int> malIds, out int anilistId);
    Task WarmAsync(int anidbId, IReadOnlyList<int> malIds, CancellationToken cancellationToken);
    Task EnsureLoadedAsync(CancellationToken cancellationToken);
}

public sealed class ArmCatalog : IArmCatalog
{
    public const string DefaultBaseUrl = "https://arm.haglund.dev";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly IOptions<AniListScrobbleOptions> _options;
    private readonly ILogger<ArmCatalog> _logger;
    private readonly string _cachePath;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<(string Source, int Id), Task<int?>> _inflight = [];
    private Dictionary<int, CachedId> _anidb = [];
    private Dictionary<int, CachedId> _mal = [];
    private bool _loaded;

    public ArmCatalog(IHttpClientFactory http, IApplicationPaths paths, IOptions<AniListScrobbleOptions> options, ILogger<ArmCatalog> logger)
        : this(
            CreateClient(http),
            Path.Combine(RequirePaths(paths).DataPath, "arm-server-cache.json"),
            options,
            logger,
            TimeProvider.System)
    {
        Directory.CreateDirectory(paths.DataPath);
    }

    internal ArmCatalog(
        HttpClient http,
        string cachePath,
        IOptions<AniListScrobbleOptions> options,
        ILogger<ArmCatalog> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _cachePath = cachePath;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public bool TryResolveAnidb(int anidbId, out int anilistId)
        => TryGet(mal: false, anidbId, out anilistId);

    public bool TryResolveMal(IReadOnlyList<int> malIds, out int anilistId)
    {
        anilistId = 0;
        if (malIds is null)
            return false;
        foreach (var malId in malIds)
        {
            if (TryGet(mal: true, malId, out anilistId))
                return true;
        }

        return false;
    }

    public async Task WarmAsync(int anidbId, IReadOnlyList<int> malIds, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (anidbId > 0)
        {
            if (IsFresh(mal: false, anidbId))
            {
                if (TryResolveAnidb(anidbId, out _))
                    return;
            }
            else
            {
                var anilistId = await LookupAsync("anidb", anidbId, cancellationToken).ConfigureAwait(false);
                if (anilistId is > 0)
                    return;
            }
        }

        foreach (var malId in malIds)
        {
            if (malId <= 0)
                continue;
            if (IsFresh(mal: true, malId))
            {
                if (TryGet(mal: true, malId, out _))
                    return;
                continue;
            }

            var anilistId = await LookupAsync("myanimelist", malId, cancellationToken).ConfigureAwait(false);
            if (anilistId is > 0)
                return;
        }
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_loaded)
                return;
        }

        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_loaded)
                    return;
            }

            if (TryReadCache(out var anidb, out var mal))
                Replace(anidb, mal, loaded: true);
            else
                Replace([], [], loaded: true);
        }
        finally
        {
            _io.Release();
        }
    }

    public static int? ParseRelation(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var trimmed = json.Trim();
        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var relation = JsonSerializer.Deserialize<ArmRelation>(trimmed, JsonOptions);
            return relation?.Anilist is > 0 ? relation.Anilist : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string IdsPath(string source, int id)
        => $"/api/v2/ids?source={Uri.EscapeDataString(source)}&id={id}&include=anilist";

    private async Task<int?> LookupAsync(string source, int id, CancellationToken cancellationToken)
    {
        Task<int?> pending;
        lock (_gate)
        {
            var key = (source, id);
            if (_inflight.TryGetValue(key, out var existing))
                pending = existing;
            else
            {
                pending = LookupCoreAsync(source, id, cancellationToken);
                _inflight[key] = pending;
            }
        }

        try
        {
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _inflight.Remove((source, id));
        }
    }

    private async Task<int?> LookupCoreAsync(string source, int id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await DownloadAsync(source, id, cancellationToken).ConfigureAwait(false);
            if (!result.Fetched)
                return null;
            Remember(source, id, result.AnilistId);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return result.AnilistId;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "ARM {Source} lookup for {Id} failed.", source, id);
            return null;
        }
    }

    private async Task<(bool Fetched, int? AnilistId)> DownloadAsync(string source, int id, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Value.RequestTimeoutSeconds, 1, 120)));
        var uri = new Uri(ResolveBaseUri(), IdsPath(source, id));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("ARM {Source} lookup for {Id} returned HTTP {Status}.", source, id, (int)response.StatusCode);
            return (false, null);
        }

        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return (true, ParseRelation(json));
    }

    private void Remember(string source, int id, int? anilistId)
    {
        var entry = new CachedId(anilistId is > 0 ? anilistId : null, _time.GetUtcNow());
        lock (_gate)
        {
            if (source == "anidb")
                _anidb = CloneAndSet(_anidb, id, entry);
            else
                _mal = CloneAndSet(_mal, id, entry);
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        Dictionary<int, CachedId> anidb;
        Dictionary<int, CachedId> mal;
        lock (_gate)
        {
            anidb = _anidb;
            mal = _mal;
        }

        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = new ArmCacheFile
            {
                Anidb = ToFileMap(anidb),
                Mal = ToFileMap(mal),
            };
            var json = JsonSerializer.Serialize(file, JsonOptions);
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temporaryPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _cachePath, true);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _logger.LogWarning(exception, "ARM cache could not be written.");
        }
        finally
        {
            _io.Release();
        }
    }

    private bool TryReadCache(out Dictionary<int, CachedId> anidb, out Dictionary<int, CachedId> mal)
    {
        anidb = [];
        mal = [];
        try
        {
            if (!File.Exists(_cachePath))
                return false;
            var json = File.ReadAllText(_cachePath);
            var file = JsonSerializer.Deserialize<ArmCacheFile>(json, JsonOptions);
            if (file is null)
                return false;
            anidb = FromFileMap(file.Anidb);
            mal = FromFileMap(file.Mal);
            return anidb.Count > 0 || mal.Count > 0;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _logger.LogWarning(exception, "ARM cache could not be read.");
            return false;
        }
    }

    private bool TryGet(bool mal, int id, out int anilistId)
    {
        anilistId = 0;
        if (id <= 0)
            return false;
        Dictionary<int, CachedId> snapshot;
        lock (_gate)
            snapshot = mal ? _mal : _anidb;
        if (snapshot.TryGetValue(id, out var cached) && cached.AnilistId is > 0)
        {
            anilistId = cached.AnilistId.Value;
            return true;
        }

        return false;
    }

    private bool IsFresh(bool mal, int id)
    {
        Dictionary<int, CachedId> snapshot;
        lock (_gate)
            snapshot = mal ? _mal : _anidb;
        if (!snapshot.TryGetValue(id, out var cached))
            return false;
        var maxAge = TimeSpan.FromHours(Math.Clamp(_options.Value.ArmCacheHours, 1, 168));
        return _time.GetUtcNow() - cached.CachedAt < maxAge;
    }

    private void Replace(Dictionary<int, CachedId> anidb, Dictionary<int, CachedId> mal, bool loaded)
    {
        lock (_gate)
        {
            _anidb = anidb;
            _mal = mal;
            _loaded = loaded;
        }
    }

    private Uri ResolveBaseUri()
    {
        var configured = _options.Value.ArmServerUrl?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
            configured = DefaultBaseUrl;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            _logger.LogWarning("ARM server URL {Url} is invalid. Using {Default}.", configured, DefaultBaseUrl);
            return new Uri(DefaultBaseUrl);
        }

        return uri;
    }

    private static HttpClient CreateClient(IHttpClientFactory http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.CreateClient("anilist-scrobble-arm");
    }

    private static IApplicationPaths RequirePaths(IApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths;
    }

    private static Dictionary<int, CachedId> CloneAndSet(Dictionary<int, CachedId> source, int id, CachedId entry)
    {
        var clone = new Dictionary<int, CachedId>(source) { [id] = entry };
        return clone;
    }

    private static Dictionary<string, ArmCacheEntry> ToFileMap(Dictionary<int, CachedId> map)
    {
        var result = new Dictionary<string, ArmCacheEntry>(StringComparer.Ordinal);
        foreach (var (id, cached) in map)
        {
            result[id.ToString()] = new ArmCacheEntry
            {
                AnilistId = cached.AnilistId,
                CachedAt = cached.CachedAt,
            };
        }

        return result;
    }

    private static Dictionary<int, CachedId> FromFileMap(Dictionary<string, ArmCacheEntry>? map)
    {
        var result = new Dictionary<int, CachedId>();
        if (map is null)
            return result;
        foreach (var (key, entry) in map)
        {
            if (!int.TryParse(key, out var id) || id <= 0 || entry is null)
                continue;
            result[id] = new CachedId(entry.AnilistId is > 0 ? entry.AnilistId : null, entry.CachedAt);
        }

        return result;
    }

    private readonly record struct CachedId(int? AnilistId, DateTimeOffset CachedAt);

    private sealed class ArmRelation
    {
        [JsonPropertyName("anilist")]
        public int? Anilist { get; set; }
    }

    private sealed class ArmCacheFile
    {
        public Dictionary<string, ArmCacheEntry> Anidb { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ArmCacheEntry> Mal { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ArmCacheEntry
    {
        public int? AnilistId { get; set; }
        public DateTimeOffset CachedAt { get; set; }
    }
}
