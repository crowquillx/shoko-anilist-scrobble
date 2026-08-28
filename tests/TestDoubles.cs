using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Anilist;
using Shoko.Abstractions.Metadata.Anilist.CrossReferences;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Events;
using Shoko.Abstractions.User.Services;

namespace Shoko.AniListScrobble.Tests;

public class DynamicFake : DispatchProxy
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<object?[], object?>> _behaviors = new(StringComparer.Ordinal);

    public DynamicFake WithValue(string propertyName, object? value)
    {
        _values[propertyName] = value;
        return this;
    }

    public DynamicFake WithBehavior(string methodName, Func<object?[], object?> behavior)
    {
        _behaviors[methodName] = behavior;
        return this;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;
        if (targetMethod.IsSpecialName && targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
            return _values.TryGetValue(targetMethod.Name[4..], out var value) ? value : Default(targetMethod.ReturnType);
        if (targetMethod.IsSpecialName && targetMethod.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            _values[targetMethod.Name[4..]] = args is { Length: > 0 } ? args[0] : null;
            return null;
        }
        if (_behaviors.TryGetValue(targetMethod.Name, out var behavior))
            return behavior(args ?? []);
        return Default(targetMethod.ReturnType);
    }

    private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    public static T Create<T>(Action<DynamicFake> configure) where T : class
    {
        var proxy = DispatchProxy.Create<T, DynamicFake>();
        configure((DynamicFake)(object)proxy);
        return proxy;
    }
}

public sealed class InMemoryStateStore : IPluginStateStore
{
    private PluginState _state = new();

    public PluginState Load() => _state;

    public void Save(PluginState state) => _state = state;
}

public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, exception));
}

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

public sealed class RecordingHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];
    public Queue<HttpResponseMessage> Responses { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        if (Responses.Count == 0)
            return JsonResponse(HttpStatusCode.OK, "{}");
        return Responses.Dequeue();
    }

    public static HttpResponseMessage JsonResponse(HttpStatusCode status, string json, IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
                response.Headers.TryAddWithoutValidation(key, value);
        }

        return response;
    }
}

public sealed class InMemoryArmCatalog : IArmCatalog
{
    public Dictionary<int, int> Map { get; } = [];
    public int EnsureCalls { get; private set; }

    public bool TryResolve(IReadOnlyList<int> malIds, out int anilistId)
    {
        anilistId = 0;
        foreach (var malId in malIds)
        {
            if (Map.TryGetValue(malId, out var id))
            {
                anilistId = id;
                return true;
            }
        }

        return false;
    }

    public Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        EnsureCalls++;
        return Task.CompletedTask;
    }
}

public sealed class FakeAniListClient : IAniListClient
{
    public List<(int MediaId, int Progress, string Status)> Saved { get; } = [];
    public AniListViewer Viewer { get; set; } = new() { Id = 9, Name = "jane" };
    public Dictionary<int, AniListMedia> Media { get; } = [];
    public Exception? Error { get; set; }
    public AniListListEntry? SaveResult { get; set; }

    public Task<AniListViewer> GetViewerAsync(CancellationToken cancellationToken) => Result(Viewer);

    public Task<AniListMedia> GetMediaAsync(int mediaId, CancellationToken cancellationToken)
    {
        if (Error is not null)
            return Task.FromException<AniListMedia>(Error);
        if (Media.TryGetValue(mediaId, out var media))
            return Task.FromResult(media);
        return Task.FromResult(new AniListMedia { Id = mediaId, Episodes = 12, Format = "TV" });
    }

    public Task<AniListListEntry> SaveProgressAsync(int mediaId, int progress, string status, CancellationToken cancellationToken)
    {
        Saved.Add((mediaId, progress, status));
        if (Error is not null)
            return Task.FromException<AniListListEntry>(Error);
        return Task.FromResult(SaveResult ?? new AniListListEntry { Id = 1, Status = status, Progress = progress, Media = new AniListMedia { Id = mediaId } });
    }

    private Task<T> Result<T>(T value) => Error is null ? Task.FromResult(value) : Task.FromException<T>(Error);
}

public sealed class FakeClientFactory : IAniListClientFactory
{
    public FakeClientFactory(FakeAniListClient client) => Client = client;
    public FakeAniListClient Client { get; }
    public string? LastToken { get; private set; }
    public IAniListClient Create(string? accessToken)
    {
        LastToken = accessToken;
        return Client;
    }
}

public static class CatalogFakes
{
    public static IUser User(int id, string name = "admin", bool admin = true)
        => DynamicFake.Create<IUser>(fake => fake.WithValue("ID", id).WithValue("Username", name).WithValue("IsAdmin", admin));

    public static IShokoSeries Series(int id, int anidbId, AnimeType type = AnimeType.TV, IReadOnlyList<int>? malIds = null, IReadOnlyList<IAnilistAnime>? anilist = null)
    {
        var anidb = DynamicFake.Create<IAnidbAnime>(fake => fake.WithValue("ID", anidbId).WithValue("MalIDs", malIds ?? []));
        return DynamicFake.Create<IShokoSeries>(fake => fake
            .WithValue("ID", id)
            .WithValue("AnidbAnimeID", anidbId)
            .WithValue("Type", type)
            .WithValue("AnidbAnime", anidb)
            .WithValue("AnilistAnime", anilist ?? [])
            .WithValue("AnilistAnimeCrossReferences", Array.Empty<IAnilistAnimeCrossReference>()));
    }

    public static IShokoEpisode Episode(
        int id,
        int number,
        IShokoSeries series,
        EpisodeType type = EpisodeType.Episode,
        bool hidden = false,
        IReadOnlyList<IAnilistEpisodeCrossReference>? xrefs = null)
        => DynamicFake.Create<IShokoEpisode>(fake => fake
            .WithValue("ID", id)
            .WithValue("EpisodeNumber", number)
            .WithValue("Type", type)
            .WithValue("IsHidden", hidden)
            .WithValue("Series", series)
            .WithValue("AnidbEpisodeID", id + 1000)
            .WithValue("AnilistEpisodeCrossReferences", xrefs ?? [])
            .WithValue("AnilistEpisodes", Array.Empty<IAnilistEpisode>()));

    public static IAnilistEpisodeCrossReference EpisodeXref(int anilistId, int episodeNumber, MatchRating rating = MatchRating.UserVerified)
        => DynamicFake.Create<IAnilistEpisodeCrossReference>(fake => fake
            .WithValue("AnilistAnimeID", anilistId)
            .WithValue("EpisodeNumber", episodeNumber)
            .WithValue("AnilistEpisodeID", anilistId * 100 + episodeNumber)
            .WithValue("MatchRating", rating));

    public static IAnilistAnime AnilistAnime(int id)
        => DynamicFake.Create<IAnilistAnime>(fake => fake.WithValue("ID", id));

    public static IEpisodeUserData UserData(IUser user, IShokoEpisode episode, bool watched, DateTime? playedAt = null)
        => DynamicFake.Create<IEpisodeUserData>(fake => fake
            .WithValue("UserID", ((IMetadata<int>)user).ID)
            .WithValue("EpisodeID", ((IMetadata<int>)episode).ID)
            .WithValue("SeriesID", ((IMetadata<int>)episode.Series!).ID)
            .WithValue("IsWatched", watched)
            .WithValue("LastPlayedAt", playedAt)
            .WithValue("User", user)
            .WithValue("Episode", episode)
            .WithValue("Series", episode.Series));

    public static IUserService Users(params IUser[] users)
        => DynamicFake.Create<IUserService>(fake => fake
            .WithBehavior("GetUsers", _ => users.AsQueryable())
            .WithBehavior("GetUserByID", args => users.FirstOrDefault(user => ((IMetadata<int>)user).ID.Equals(args[0]))));

    public static EpisodeUserDataSavedEventArgs Event(
        IUser user,
        IShokoEpisode episode,
        IEpisodeUserData data,
        VideoUserDataSaveReason videoReason,
        EpisodeUserDataSaveReason reason)
        => new()
        {
            User = user,
            Episode = episode,
            UserData = data,
            VideoReason = videoReason,
            Reason = reason,
        };
}
