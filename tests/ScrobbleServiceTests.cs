using Microsoft.Extensions.Options;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Events;
using Xunit;

namespace Shoko.AniListScrobble.Tests;

public sealed class ScrobbleServiceTests
{
    private static readonly EpisodeUserDataSaveReason Watch = EpisodeUserDataSaveReason.LastPlayedAt | EpisodeUserDataSaveReason.PlaybackCount;

    [Fact]
    public void StatusOmitsAccessToken()
    {
        var store = new InMemoryStateStore();
        store.Save(new PluginState { AccessToken = "secret-token", AnilistUsername = "jane" });
        var service = Create(store, new FakeAniListClient());

        var status = service.GetStatus();
        var json = System.Text.Json.JsonSerializer.Serialize(status);

        Assert.True(status.Connected);
        Assert.Equal("jane", status.AnilistUsername);
        Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessToken", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TokenConnectStoresTokenWithoutExposingIt()
    {
        var store = new InMemoryStateStore();
        var service = Create(store, new FakeAniListClient());

        var result = await service.ConnectTokenAsync("secret-token", CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(service.GetStatus());

        Assert.Equal("jane", result.Username);
        Assert.Equal("secret-token", store.Load().AccessToken);
        Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectClearsTheToken()
    {
        var store = new InMemoryStateStore();
        store.Save(new PluginState { AccessToken = "secret-token", AnilistUsername = "jane" });
        var service = Create(store, new FakeAniListClient());

        await service.DisconnectAsync(CancellationToken.None);

        Assert.Null(store.Load().AccessToken);
        Assert.False(service.GetStatus().Connected);
    }

    [Fact]
    public async Task PlaybackEndScrobblesProgressAndCompletesAtLastEpisode()
    {
        var client = new FakeAniListClient
        {
            Media = { [20958] = new AniListMedia { Id = 20958, Episodes = 4, Format = "TV" } },
        };
        var store = AuthedStore();
        var service = Create(store, client);
        var args = PlaybackEvent(watched: true, episodeNumber: 4, anilistId: 20958);

        await service.HandleEventAsync(args, CancellationToken.None);

        var saved = Assert.Single(client.Saved);
        Assert.Equal(20958, saved.MediaId);
        Assert.Equal(4, saved.Progress);
        Assert.Equal("COMPLETED", saved.Status);
        Assert.Equal(1, store.Load().Counters.Scrobbled);
        Assert.Equal(1, store.Load().Counters.Completed);
    }

    [Fact]
    public async Task MidSeriesWatchSetsCurrentProgressToEpisodeIndex()
    {
        var client = new FakeAniListClient
        {
            Media = { [20958] = new AniListMedia { Id = 20958, Episodes = 12, Format = "TV", MediaListEntry = new AniListListEntry { Progress = 2, Status = "CURRENT" } } },
        };
        var service = Create(AuthedStore(), client);

        await service.HandleEventAsync(PlaybackEvent(true, 5, 20958), CancellationToken.None);

        var saved = Assert.Single(client.Saved);
        Assert.Equal(5, saved.Progress);
        Assert.Equal("CURRENT", saved.Status);
    }

    [Fact]
    public async Task DoesNotDecreaseAniListProgress()
    {
        var client = new FakeAniListClient
        {
            Media = { [20958] = new AniListMedia { Id = 20958, Episodes = 12, Format = "TV", MediaListEntry = new AniListListEntry { Progress = 10, Status = "CURRENT" } } },
        };
        var service = Create(AuthedStore(), client);

        await service.HandleEventAsync(PlaybackEvent(true, 3, 20958), CancellationToken.None);

        Assert.Empty(client.Saved);
    }

    [Fact]
    public async Task DuplicateEventIsNotSentTwice()
    {
        var client = new FakeAniListClient
        {
            Media = { [20958] = new AniListMedia { Id = 20958, Episodes = 12, Format = "TV" } },
        };
        var service = Create(AuthedStore(), client);
        var args = PlaybackEvent(true, 2, 20958);

        await service.HandleEventAsync(args, CancellationToken.None);
        await service.HandleEventAsync(args, CancellationToken.None);

        Assert.Single(client.Saved);
    }

    [Fact]
    public async Task WebUiMarkDoesNotScrobble()
    {
        var client = new FakeAniListClient();
        var service = Create(AuthedStore(), client);
        var args = Event(true, 4, 20958, VideoUserDataSaveReason.None, Watch);

        await service.HandleEventAsync(args, CancellationToken.None);

        Assert.Empty(client.Saved);
    }

    [Fact]
    public async Task ImportDoesNotScrobble()
    {
        var client = new FakeAniListClient();
        var service = Create(AuthedStore(), client);
        var args = Event(true, 4, 20958, VideoUserDataSaveReason.PlaybackEnd, Watch | EpisodeUserDataSaveReason.Import);

        await service.HandleEventAsync(args, CancellationToken.None);

        Assert.Empty(client.Saved);
    }

    [Fact]
    public async Task UnwatchDoesNotScrobble()
    {
        var client = new FakeAniListClient();
        var service = Create(AuthedStore(), client);

        await service.HandleEventAsync(PlaybackEvent(false, 4, 20958), CancellationToken.None);

        Assert.Empty(client.Saved);
    }

    [Fact]
    public async Task OtherUserIsIgnored()
    {
        var client = new FakeAniListClient();
        var service = Create(AuthedStore(), client, shokoUserId: 1);
        var user = CatalogFakes.User(99, "other", admin: false);
        var series = CatalogFakes.Series(3, 10944, anilist: [CatalogFakes.AnilistAnime(20958)]);
        var episode = CatalogFakes.Episode(22, 4, series);
        var data = CatalogFakes.UserData(user, episode, true);
        var args = CatalogFakes.Event(user, episode, data, VideoUserDataSaveReason.PlaybackEnd, Watch);

        await service.HandleEventAsync(args, CancellationToken.None);

        Assert.Empty(client.Saved);
    }

    [Fact]
    public async Task MovieWatchMarksCompleted()
    {
        var client = new FakeAniListClient
        {
            Media = { [12] = new AniListMedia { Id = 12, Episodes = 1, Format = "MOVIE" } },
        };
        var store = AuthedStore();
        var service = Create(store, client);
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 888, Shoko.Abstractions.Metadata.Enums.AnimeType.Movie, anilist: [CatalogFakes.AnilistAnime(12)]);
        var episode = CatalogFakes.Episode(9, 1, series);
        var data = CatalogFakes.UserData(user, episode, true);

        await service.HandleEventAsync(CatalogFakes.Event(user, episode, data, VideoUserDataSaveReason.PlaybackEnd, Watch), CancellationToken.None);

        var saved = Assert.Single(client.Saved);
        Assert.Equal(1, saved.Progress);
        Assert.Equal("COMPLETED", saved.Status);
    }

    [Fact]
    public async Task RepeatingStatusIsPreserved()
    {
        var client = new FakeAniListClient
        {
            Media = { [20958] = new AniListMedia { Id = 20958, Episodes = 12, Format = "TV", MediaListEntry = new AniListListEntry { Progress = 1, Status = "REPEATING" } } },
        };
        var service = Create(AuthedStore(), client);

        await service.HandleEventAsync(PlaybackEvent(true, 2, 20958), CancellationToken.None);

        Assert.Equal("REPEATING", Assert.Single(client.Saved).Status);
    }

    [Fact]
    public async Task CompletedShowIsNotReopened()
    {
        var client = new FakeAniListClient
        {
            Media = { [20958] = new AniListMedia { Id = 20958, Episodes = 12, Format = "TV", MediaListEntry = new AniListListEntry { Progress = 12, Status = "COMPLETED" } } },
        };
        var service = Create(AuthedStore(), client);

        await service.HandleEventAsync(PlaybackEvent(true, 1, 20958), CancellationToken.None);

        Assert.Empty(client.Saved);
    }

    [Fact]
    public async Task UnauthorizedClearsTheToken()
    {
        var client = new FakeAniListClient
        {
            Error = new AniListRequestException("AniList rejected the access token.", 401, false),
        };
        var store = AuthedStore();
        var service = Create(store, client);

        await service.HandleEventAsync(PlaybackEvent(true, 2, 20958), CancellationToken.None);

        Assert.Null(store.Load().AccessToken);
        Assert.False(service.GetStatus().Connected);
    }

    [Fact]
    public async Task ArmFallbackIsUsedWhenShokoHasOnlyMal()
    {
        var client = new FakeAniListClient
        {
            Media = { [5114] = new AniListMedia { Id = 5114, Episodes = 64, Format = "TV" } },
        };
        var arm = new InMemoryArmCatalog { Map = { [5114] = 5114 } };
        var service = Create(AuthedStore(), client, arm: arm);
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 777, malIds: [5114]);
        var episode = CatalogFakes.Episode(9, 8, series);
        var data = CatalogFakes.UserData(user, episode, true);

        await service.HandleEventAsync(CatalogFakes.Event(user, episode, data, VideoUserDataSaveReason.PlaybackEnd, Watch), CancellationToken.None);

        var saved = Assert.Single(client.Saved);
        Assert.Equal(5114, saved.MediaId);
        Assert.Equal(8, saved.Progress);
        Assert.Equal(1, arm.EnsureCalls);
    }

    [Fact]
    public void ProgressPlannerCompletesWhenIndexReachesEpisodeCount()
    {
        var watch = new MappedWatch(1, 2, 3, 9, 12, false, MappingSource.ShokoSeries, []);
        var media = new AniListMedia { Id = 9, Episodes = 12, Format = "TV" };
        var plan = ProgressPlanner.Plan(watch, media);
        Assert.True(plan.Write);
        Assert.Equal("COMPLETED", plan.Status);
        Assert.Equal(12, plan.Progress);
    }

    [Fact]
    public void ProgressPlannerKeepsAiringShowsCurrentWhenEpisodeCountIsUnknown()
    {
        var watch = new MappedWatch(1, 2, 3, 9, 5, false, MappingSource.ShokoSeries, []);
        var media = new AniListMedia { Id = 9, Episodes = null, Format = "TV" };
        var plan = ProgressPlanner.Plan(watch, media);
        Assert.Equal("CURRENT", plan.Status);
        Assert.True(plan.Write);
    }

    private static PluginState AuthedStoreState() => new() { AccessToken = "tok", AnilistUsername = "jane" };

    private static InMemoryStateStore AuthedStore()
    {
        var store = new InMemoryStateStore();
        store.Save(AuthedStoreState());
        return store;
    }

    private static EpisodeUserDataSavedEventArgs PlaybackEvent(bool watched, int episodeNumber, int anilistId)
        => Event(watched, episodeNumber, anilistId, VideoUserDataSaveReason.PlaybackEnd, Watch);

    private static EpisodeUserDataSavedEventArgs Event(bool watched, int episodeNumber, int anilistId, VideoUserDataSaveReason video, EpisodeUserDataSaveReason reason)
    {
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 10944, anilist: [CatalogFakes.AnilistAnime(anilistId)]);
        var episode = CatalogFakes.Episode(22, episodeNumber, series);
        var data = CatalogFakes.UserData(user, episode, watched);
        return CatalogFakes.Event(user, episode, data, video, reason);
    }

    private static AniListScrobbleService Create(
        InMemoryStateStore store,
        FakeAniListClient client,
        int shokoUserId = 1,
        IArmCatalog? arm = null)
    {
        var user = CatalogFakes.User(1);
        var options = new AniListScrobbleOptions
        {
            Enabled = true,
            ClientId = "abc",
            ShokoUserId = shokoUserId,
            AcceptJellyfinToggles = true,
        };
        return new AniListScrobbleService(
            store,
            Options.Create(options),
            new FakeClientFactory(client),
            arm ?? new InMemoryArmCatalog(),
            CatalogFakes.Users(user),
            new RecordingLogger<AniListScrobbleService>());
    }
}
