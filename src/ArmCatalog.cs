using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shoko.Abstractions.Plugin;

namespace Shoko.AniListScrobble;

public interface IArmCatalog
{
    bool TryResolve(IReadOnlyList<int> malIds, out int anilistId);
    Task EnsureLoadedAsync(CancellationToken cancellationToken);
}

public sealed class ArmCatalog : IArmCatalog
{
    public const string DefaultUrl = "https://raw.githubusercontent.com/kawaiioverflow/arm/master/arm.json";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IHttpClientFactory _http;
    private readonly IOptions<AniListScrobbleOptions> _options;
    private readonly ILogger<ArmCatalog> _logger;
    private readonly string _cachePath;
    private readonly object _gate = new();
    private Dictionary<int, int> _malToAnilist = new();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public ArmCatalog(IHttpClientFactory http, IApplicationPaths paths, IOptions<AniListScrobbleOptions> options, ILogger<ArmCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _options = options;
        _logger = logger;
        Directory.CreateDirectory(paths.DataPath);
        _cachePath = Path.Combine(paths.DataPath, "arm-mal-anilist.json");
    }

    public bool TryResolve(IReadOnlyList<int> malIds, out int anilistId)
    {
        anilistId = 0;
        Dictionary<int, int> map;
        lock (_gate)
            map = _malToAnilist;
        foreach (var malId in malIds)
        {
            if (malId > 0 && map.TryGetValue(malId, out var id) && id > 0)
            {
                anilistId = id;
                return true;
            }
        }

        return false;
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        var maxAge = TimeSpan.FromHours(Math.Clamp(_options.Value.ArmCacheHours, 1, 168));
        lock (_gate)
        {
            if (_malToAnilist.Count > 0 && DateTimeOffset.UtcNow - _loadedAt < maxAge)
                return;
        }

        if (TryLoadCache(maxAge, out var cached))
        {
            Replace(cached);
            return;
        }

        try
        {
            var map = await DownloadAsync(cancellationToken).ConfigureAwait(false);
            Replace(map);
            WriteCache(map);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (TryLoadCache(TimeSpan.FromDays(365), out cached) && cached.Count > 0)
            {
                _logger.LogWarning(exception, "ARM download failed. Using stale cache with {Count} MAL mappings.", cached.Count);
                Replace(cached);
                return;
            }

            _logger.LogWarning(exception, "ARM download failed and no cache is available.");
        }
    }

    public static Dictionary<int, int> Parse(string json)
    {
        var entries = JsonSerializer.Deserialize<List<ArmEntry>>(json, JsonOptions) ?? [];
        var map = new Dictionary<int, int>();
        foreach (var entry in entries)
        {
            if (entry.MalId is > 0 && entry.AnilistId is > 0)
                map[entry.MalId.Value] = entry.AnilistId.Value;
        }

        return map;
    }

    private async Task<Dictionary<int, int>> DownloadAsync(CancellationToken cancellationToken)
    {
        var client = _http.CreateClient("anilist-scrobble-arm");
        using var response = await client.GetAsync(DefaultUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    private bool TryLoadCache(TimeSpan maxAge, out Dictionary<int, int> map)
    {
        map = [];
        try
        {
            if (!File.Exists(_cachePath))
                return false;
            var info = new FileInfo(_cachePath);
            if (DateTimeOffset.UtcNow - info.LastWriteTimeUtc > maxAge)
                return false;
            var json = File.ReadAllText(_cachePath);
            map = JsonSerializer.Deserialize<Dictionary<int, int>>(json, JsonOptions) ?? [];
            return map.Count > 0;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _logger.LogWarning(exception, "ARM cache could not be read.");
            return false;
        }
    }

    private void WriteCache(Dictionary<int, int> map)
    {
        try
        {
            var json = JsonSerializer.Serialize(map, JsonOptions);
            var temporaryPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _cachePath, true);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "ARM cache could not be written.");
        }
    }

    private void Replace(Dictionary<int, int> map)
    {
        lock (_gate)
        {
            _malToAnilist = map;
            _loadedAt = DateTimeOffset.UtcNow;
        }
    }

    private sealed class ArmEntry
    {
        [JsonPropertyName("mal_id")]
        public int? MalId { get; set; }

        [JsonPropertyName("anilist_id")]
        public int? AnilistId { get; set; }
    }
}
