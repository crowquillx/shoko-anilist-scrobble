using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shoko.Abstractions.Config.Attributes;
using Shoko.Abstractions.Plugin;
using ShokoConfiguration = Shoko.Abstractions.Config.IConfiguration;

namespace Shoko.AniListScrobble;

public sealed class AniListScrobbleOptions : ShokoConfiguration
{
    public const string SectionName = "Shoko:Plugins:AniListScrobble";

    [EnvironmentVariable("SHOKO__PLUGINS__ANILISTSCROBBLE__ENABLED")]
    [RequiresRestart]
    public bool Enabled { get; set; } = true;

    [EnvironmentVariable("SHOKO__PLUGINS__ANILISTSCROBBLE__CLIENTID")]
    [RequiresRestart]
    public string? ClientId { get; set; }

    [Range(0, int.MaxValue)]
    [RequiresRestart]
    public int ShokoUserId { get; set; }

    [RequiresRestart]
    public bool AcceptJellyfinToggles { get; set; } = true;

    [Range(1, 120)]
    [RequiresRestart]
    public int RequestTimeoutSeconds { get; set; } = 20;

    [Range(1024, 10_485_760)]
    [RequiresRestart]
    public int MaxJsonResponseBytes { get; set; } = 1_048_576;

    [Range(1, 168)]
    [RequiresRestart]
    public int ArmCacheHours { get; set; } = 168;

    [RequiresRestart]
    public string ArmServerUrl { get; set; } = ArmCatalog.DefaultBaseUrl;

    public void CopyFrom(AniListScrobbleOptions source)
    {
        Enabled = source.Enabled;
        ClientId = source.ClientId;
        ShokoUserId = source.ShokoUserId;
        AcceptJellyfinToggles = source.AcceptJellyfinToggles;
        RequestTimeoutSeconds = source.RequestTimeoutSeconds;
        MaxJsonResponseBytes = source.MaxJsonResponseBytes;
        ArmCacheHours = source.ArmCacheHours;
        ArmServerUrl = source.ArmServerUrl;
    }
}

public sealed class PluginState
{
    public int SchemaVersion { get; set; } = 1;
    public string? AccessToken { get; set; }
    public string? AnilistUsername { get; set; }
    public int? AnilistUserId { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public Dictionary<string, ScrobbledEpisode> Scrobbled { get; set; } = new(StringComparer.Ordinal);
    public ScrobbleCounters Counters { get; set; } = new();
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public LastScrobble? LastScrobble { get; set; }
}

public sealed class ScrobbledEpisode
{
    public int EpisodeId { get; set; }
    public int AnilistId { get; set; }
    public int Progress { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset ScrobbledAt { get; set; }
}

public sealed class LastScrobble
{
    public int AnilistId { get; set; }
    public int Progress { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class ScrobbleCounters
{
    public int Scrobbled { get; set; }
    public int Completed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
}

public interface IPluginStateStore
{
    PluginState Load();
    void Save(PluginState state);
}

public sealed class AtomicPluginStateStore : IPluginStateStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public AtomicPluginStateStore(IApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataPath);
        _path = Path.Combine(paths.DataPath, "shoko-anilist-scrobble-state.json");
    }

    public PluginState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
                return new PluginState();
            try
            {
                using var stream = File.OpenRead(_path);
                return JsonSerializer.Deserialize<PluginState>(stream, JsonOptions) ?? new PluginState();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The AniList scrobble state file is invalid.", exception);
            }
            catch (IOException exception)
            {
                throw new IOException("The AniList scrobble state file could not be read.", exception);
            }
        }
    }

    public void Save(PluginState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, state, JsonOptions);
                    stream.Flush(true);
                }

                File.Move(temporaryPath, _path, true);
                TryRestrictAccess(_path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }

    private static void TryRestrictAccess(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best effort. Shoko's data directory is already private.
        }
    }
}
