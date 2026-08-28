using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Events;
using Shoko.Abstractions.User.Services;

namespace Shoko.AniListScrobble;

public sealed record StatusSnapshot(
    string ApiVersion,
    string PluginVersion,
    bool Enabled,
    bool Connected,
    string? AnilistUsername,
    int? ShokoUserId,
    string? ShokoUsername,
    ScrobbleCounters Counters,
    int ScrobbledCount,
    LastScrobble? LastScrobble,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    IReadOnlyList<CapabilityDto> Capabilities);

public sealed record TokenConnectResult(string? Username, int? UserId);

public interface IAniListClientFactory
{
    IAniListClient Create(string? accessToken);
}

public sealed class AniListClientFactory : IAniListClientFactory
{
    private readonly IHttpClientFactory _http;
    private readonly IOptions<AniListScrobbleOptions> _options;

    public AniListClientFactory(IHttpClientFactory http, IOptions<AniListScrobbleOptions> options)
    {
        _http = http;
        _options = options;
    }

    public IAniListClient Create(string? accessToken)
    {
        var options = _options.Value;
        var version = typeof(AniListScrobblePlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return new AniListClient(_http.CreateClient("anilist-scrobble"), new AniListClientOptions
        {
            AccessToken = accessToken,
            AppVersion = version,
            MaxJsonResponseBytes = options.MaxJsonResponseBytes,
            RequestTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
        });
    }
}

public interface IAniListScrobbleService
{
    StatusSnapshot GetStatus();
    string GetAuthorizeUrl();
    Task<TokenConnectResult> ConnectTokenAsync(string accessToken, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task HandleEventAsync(EpisodeUserDataSavedEventArgs args, CancellationToken cancellationToken);
}

public sealed class AniListScrobbleService : IAniListScrobbleService
{
    public const string ApiVersion = "1";
    private readonly IPluginStateStore _store;
    private readonly IOptions<AniListScrobbleOptions> _options;
    private readonly IAniListClientFactory _clients;
    private readonly IArmCatalog _arm;
    private readonly IUserService _users;
    private readonly ILogger<AniListScrobbleService> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public AniListScrobbleService(
        IPluginStateStore store,
        IOptions<AniListScrobbleOptions> options,
        IAniListClientFactory clients,
        IArmCatalog arm,
        IUserService users,
        ILogger<AniListScrobbleService> logger)
    {
        _store = store;
        _options = options;
        _clients = clients;
        _arm = arm;
        _users = users;
        _logger = logger;
    }

    public StatusSnapshot GetStatus()
    {
        var options = _options.Value;
        var state = _store.Load();
        var user = ResolveUser(options);
        var connected = !string.IsNullOrWhiteSpace(state.AccessToken);
        return new StatusSnapshot(
            ApiVersion,
            typeof(AniListScrobblePlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            options.Enabled,
            connected,
            state.AnilistUsername,
            EpisodeMapper.MetadataId(user),
            user?.Username,
            state.Counters,
            state.Scrobbled.Count,
            state.LastScrobble,
            state.LastError,
            state.LastErrorAt,
            [
                new CapabilityDto("scrobble", options.Enabled && connected),
                new CapabilityDto("jellyfin-toggles", options.Enabled && options.AcceptJellyfinToggles && connected),
                new CapabilityDto("arm-fallback", options.Enabled),
                new CapabilityDto("pin-auth", options.Enabled && !string.IsNullOrWhiteSpace(options.ClientId)),
            ]);
    }

    public string GetAuthorizeUrl()
    {
        EnsureEnabled();
        var clientId = _options.Value.ClientId?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Configure an AniList client ID before connecting.");
        return AniListClient.AuthorizeUrl(clientId);
    }

    public async Task<TokenConnectResult> ConnectTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Paste the AniList access token.", nameof(accessToken));
        var token = accessToken.Trim();
        var client = _clients.Create(token);
        var viewer = await client.GetViewerAsync(cancellationToken).ConfigureAwait(false);
        var state = _store.Load();
        state.AccessToken = token;
        state.AnilistUsername = viewer.Name;
        state.AnilistUserId = viewer.Id > 0 ? viewer.Id : null;
        state.ConnectedAt = DateTimeOffset.UtcNow;
        state.LastError = null;
        _store.Save(state);
        return new TokenConnectResult(viewer.Name, viewer.Id > 0 ? viewer.Id : null);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _store.Load();
        state.AccessToken = null;
        state.AnilistUsername = null;
        state.AnilistUserId = null;
        state.ConnectedAt = null;
        _store.Save(state);
        return Task.CompletedTask;
    }

    public async Task HandleEventAsync(EpisodeUserDataSavedEventArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = _options.Value;
        var user = ResolveUser(options);
        var configuredUserId = EpisodeMapper.MetadataId(user);
        var candidate = new WatchCandidate(
            EpisodeMapper.MetadataId(args.User) ?? args.UserData.UserID,
            args.UserData.IsWatched,
            args.IsImport || args.Reason.HasFlag(EpisodeUserDataSaveReason.Import),
            args.Reason,
            args.VideoReason);
        var skip = ScrobbleGate.Decide(candidate, options, configuredUserId);
        if (skip is not SkipReason.None)
        {
            RecordSkip(skip);
            return;
        }

        await _arm.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (!EpisodeMapper.TryMap(args.Episode, args.User, args.UserData, _arm, out var mapped, out skip))
        {
            RecordSkip(skip);
            return;
        }

        await ScrobbleAsync(mapped, cancellationToken).ConfigureAwait(false);
    }

    internal async Task ScrobbleAsync(MappedWatch watch, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _store.Load();
            if (AlreadyScrobbled(state, watch))
            {
                state.Counters.Skipped++;
                _store.Save(state);
                return;
            }

            if (string.IsNullOrWhiteSpace(state.AccessToken))
                return;

            var client = _clients.Create(state.AccessToken);
            try
            {
                var media = await client.GetMediaAsync(watch.AnilistId, cancellationToken).ConfigureAwait(false);
                var plan = ProgressPlanner.Plan(watch, media);
                if (!plan.Write)
                {
                    Remember(state, watch, plan.Progress, plan.Status);
                    state.Counters.Skipped++;
                    state.LastError = null;
                    _store.Save(state);
                    return;
                }

                var saved = await client.SaveProgressAsync(watch.AnilistId, plan.Progress, plan.Status!, cancellationToken).ConfigureAwait(false);
                Remember(state, watch, saved.Progress, saved.Status);
                state.Counters.Scrobbled++;
                if (string.Equals(saved.Status, ProgressPlanner.Completed, StringComparison.OrdinalIgnoreCase))
                    state.Counters.Completed++;
                state.LastScrobble = new LastScrobble
                {
                    AnilistId = watch.AnilistId,
                    Progress = saved.Progress,
                    Status = saved.Status,
                    At = DateTimeOffset.UtcNow,
                };
                state.LastError = null;
                _store.Save(state);
            }
            catch (AniListRequestException exception) when (exception.IsUnauthorized)
            {
                ClearToken(state);
                RecordError(state, exception.Message);
                _store.Save(state);
            }
            catch (AniListRequestException exception) when (exception.Retryable)
            {
                state.Counters.Failed++;
                RecordError(state, exception.Message);
                _store.Save(state);
                if (exception.RetryAfter is { } retry)
                    await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                state.Counters.Failed++;
                RecordError(state, exception is AniListRequestException anilist ? anilist.Message : "AniList scrobble failed.");
                _store.Save(state);
                _logger.LogWarning(exception, "AniList scrobble failed for media {AnilistId} progress {Progress}.", watch.AnilistId, watch.ProgressIndex);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private IUser? ResolveUser(AniListScrobbleOptions options)
    {
        if (options.ShokoUserId > 0)
            return _users.GetUserByID(options.ShokoUserId);
        return _users.GetUsers().FirstOrDefault(user => user.IsAdmin) ?? _users.GetUsers().FirstOrDefault();
    }

    private void EnsureEnabled()
    {
        if (!_options.Value.Enabled)
            throw new InvalidOperationException("AniList scrobble is disabled.");
    }

    private void RecordSkip(SkipReason skip)
    {
        if (skip is SkipReason.Disabled or SkipReason.Unwatch or SkipReason.NotAWatch or SkipReason.PlaybackNoise or SkipReason.ManualUi or SkipReason.WrongUser)
            return;
        var state = _store.Load();
        state.Counters.Skipped++;
        _store.Save(state);
    }

    private static bool AlreadyScrobbled(PluginState state, MappedWatch watch)
    {
        var key = EpisodeMapper.ScrobbledKey(watch.UserId, watch.EpisodeId);
        if (!state.Scrobbled.TryGetValue(key, out var previous))
            return false;
        return previous.AnilistId == watch.AnilistId && previous.Progress >= watch.ProgressIndex;
    }

    private static void Remember(PluginState state, MappedWatch watch, int progress, string? status)
    {
        state.Scrobbled[EpisodeMapper.ScrobbledKey(watch.UserId, watch.EpisodeId)] = new ScrobbledEpisode
        {
            EpisodeId = watch.EpisodeId,
            AnilistId = watch.AnilistId,
            Progress = progress,
            Status = status,
            ScrobbledAt = DateTimeOffset.UtcNow,
        };
    }

    private static void ClearToken(PluginState state)
    {
        state.AccessToken = null;
        state.AnilistUsername = null;
        state.AnilistUserId = null;
        state.ConnectedAt = null;
    }

    private static void RecordError(PluginState state, string message)
    {
        state.LastError = message;
        state.LastErrorAt = DateTimeOffset.UtcNow;
    }
}
