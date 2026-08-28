using Shoko.Abstractions.Metadata.Enums;
using Xunit;

namespace Shoko.AniListScrobble.Tests;

public sealed class MappingTests
{
    [Fact]
    public void UsesShokoSeriesAnilistIdAndAnidbEpisodeNumber()
    {
        var user = CatalogFakes.User(4);
        var series = CatalogFakes.Series(11, 10944, malIds: [25777], anilist: [CatalogFakes.AnilistAnime(20958)]);
        var episode = CatalogFakes.Episode(22, 4, series);
        var data = CatalogFakes.UserData(user, episode, watched: true);
        var arm = new InMemoryArmCatalog();

        Assert.True(EpisodeMapper.TryMap(episode, user, data, arm, out var mapped, out var skip));
        Assert.Equal(SkipReason.None, skip);
        Assert.Equal(20958, mapped.AnilistId);
        Assert.Equal(4, mapped.ProgressIndex);
        Assert.Equal(MappingSource.ShokoSeries, mapped.Source);
        Assert.Equal(10944, mapped.AnidbAnimeId);
        Assert.Equal([25777], mapped.MalIds);
    }

    [Fact]
    public void PrefersEpisodeLevelAnilistXrefForSeasonSplits()
    {
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 10944, anilist: [CatalogFakes.AnilistAnime(111)]);
        var xref = CatalogFakes.EpisodeXref(222, 1);
        var episode = CatalogFakes.Episode(9, 13, series, xrefs: [xref]);
        var data = CatalogFakes.UserData(user, episode, watched: true);

        Assert.True(EpisodeMapper.TryMap(episode, user, data, new InMemoryArmCatalog(), out var mapped, out _));
        Assert.Equal(222, mapped.AnilistId);
        Assert.Equal(1, mapped.ProgressIndex);
        Assert.Equal(MappingSource.ShokoEpisode, mapped.Source);
    }

    [Fact]
    public void FallsBackToArmAnidbWhenShokoHasNoAnilistLink()
    {
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 10944, malIds: [5114]);
        var episode = CatalogFakes.Episode(9, 7, series);
        var data = CatalogFakes.UserData(user, episode, watched: true);
        var arm = new InMemoryArmCatalog { AnidbMap = { [10944] = 20958 }, Map = { [5114] = 5114 } };

        Assert.True(EpisodeMapper.TryMap(episode, user, data, arm, out var mapped, out _));
        Assert.Equal(20958, mapped.AnilistId);
        Assert.Equal(7, mapped.ProgressIndex);
        Assert.Equal(MappingSource.ArmAnidb, mapped.Source);
    }

    [Fact]
    public void FallsBackToArmMalWhenAnidbArmMisses()
    {
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 10944, malIds: [5114]);
        var episode = CatalogFakes.Episode(9, 7, series);
        var data = CatalogFakes.UserData(user, episode, watched: true);
        var arm = new InMemoryArmCatalog { Map = { [5114] = 5114 } };

        Assert.True(EpisodeMapper.TryMap(episode, user, data, arm, out var mapped, out _));
        Assert.Equal(5114, mapped.AnilistId);
        Assert.Equal(7, mapped.ProgressIndex);
        Assert.Equal(MappingSource.ArmMal, mapped.Source);
    }

    [Fact]
    public void MoviesUseProgressIndexOne()
    {
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 888, AnimeType.Movie, anilist: [CatalogFakes.AnilistAnime(12)]);
        var episode = CatalogFakes.Episode(9, 1, series);
        var data = CatalogFakes.UserData(user, episode, watched: true);

        Assert.True(EpisodeMapper.TryMap(episode, user, data, new InMemoryArmCatalog(), out var mapped, out _));
        Assert.True(mapped.IsMovie);
        Assert.Equal(1, mapped.ProgressIndex);
    }

    [Theory]
    [InlineData(EpisodeType.Special)]
    [InlineData(EpisodeType.Credits)]
    [InlineData(EpisodeType.Trailer)]
    [InlineData(EpisodeType.Parody)]
    [InlineData(EpisodeType.Other)]
    public void SkipsNonRegularEpisodeTypes(EpisodeType type)
    {
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 10944, anilist: [CatalogFakes.AnilistAnime(1)]);
        var episode = CatalogFakes.Episode(9, 1, series, type);
        var data = CatalogFakes.UserData(user, episode, watched: true);

        Assert.False(EpisodeMapper.TryMap(episode, user, data, new InMemoryArmCatalog(), out _, out var skip));
        Assert.Equal(SkipReason.UnsupportedEpisodeType, skip);
    }

    [Fact]
    public void SkipsHiddenEpisodes()
    {
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 10944, anilist: [CatalogFakes.AnilistAnime(1)]);
        var episode = CatalogFakes.Episode(9, 1, series, hidden: true);
        var data = CatalogFakes.UserData(user, episode, watched: true);

        Assert.False(EpisodeMapper.TryMap(episode, user, data, new InMemoryArmCatalog(), out _, out var skip));
        Assert.Equal(SkipReason.Hidden, skip);
    }

    [Fact]
    public void SkipsWhenNoAnilistMappingExists()
    {
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 10944, malIds: [999]);
        var episode = CatalogFakes.Episode(9, 1, series);
        var data = CatalogFakes.UserData(user, episode, watched: true);

        Assert.False(EpisodeMapper.TryMap(episode, user, data, new InMemoryArmCatalog(), out _, out var skip));
        Assert.Equal(SkipReason.MissingAnilist, skip);
    }

    [Fact]
    public void PrefersUserVerifiedXrefOverWeakerMatches()
    {
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 10944);
        var weak = CatalogFakes.EpisodeXref(100, 2, MatchRating.FirstAvailable);
        var strong = CatalogFakes.EpisodeXref(200, 3, MatchRating.UserVerified);
        var episode = CatalogFakes.Episode(9, 14, series, xrefs: [weak, strong]);
        var data = CatalogFakes.UserData(user, episode, watched: true);

        Assert.True(EpisodeMapper.TryMap(episode, user, data, new InMemoryArmCatalog(), out var mapped, out _));
        Assert.Equal(200, mapped.AnilistId);
        Assert.Equal(3, mapped.ProgressIndex);
    }

    [Fact]
    public void SkipsMissingAniDb()
    {
        var user = CatalogFakes.User(1);
        var series = CatalogFakes.Series(3, 0, anilist: [CatalogFakes.AnilistAnime(1)]);
        var episode = CatalogFakes.Episode(9, 1, series);
        var data = CatalogFakes.UserData(user, episode, watched: true);

        Assert.False(EpisodeMapper.TryMap(episode, user, data, new InMemoryArmCatalog(), out _, out var skip));
        Assert.Equal(SkipReason.MissingAniDb, skip);
    }
}
