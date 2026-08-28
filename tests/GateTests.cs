using Shoko.Abstractions.User.Enums;
using Xunit;

namespace Shoko.AniListScrobble.Tests;

public sealed class GateTests
{
    private static readonly AniListScrobbleOptions Enabled = new() { Enabled = true, AcceptJellyfinToggles = true };
    private static readonly EpisodeUserDataSaveReason Watch = EpisodeUserDataSaveReason.LastPlayedAt | EpisodeUserDataSaveReason.PlaybackCount;

    [Fact]
    public void PlaybackEndFromJellyfinIsAccepted()
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, true, false, Watch, VideoUserDataSaveReason.PlaybackEnd), Enabled, 1);
        Assert.Equal(SkipReason.None, skip);
    }

    [Fact]
    public void PlaybackProgressAutoWatchIsAccepted()
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, true, false, Watch, VideoUserDataSaveReason.PlaybackProgress), Enabled, 1);
        Assert.Equal(SkipReason.None, skip);
    }

    [Fact]
    public void JellyfinToggleIsAcceptedWhenEnabled()
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, true, false, Watch, VideoUserDataSaveReason.UserInteraction), Enabled, 1);
        Assert.Equal(SkipReason.None, skip);
    }

    [Fact]
    public void JellyfinToggleIsRejectedWhenDisabled()
    {
        var options = new AniListScrobbleOptions { Enabled = true, AcceptJellyfinToggles = false };
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, true, false, Watch, VideoUserDataSaveReason.UserInteraction), options, 1);
        Assert.Equal(SkipReason.ManualUi, skip);
    }

    [Fact]
    public void WebUiMarkWithNoVideoReasonIsRejected()
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, true, false, Watch, VideoUserDataSaveReason.None), Enabled, 1);
        Assert.Equal(SkipReason.ManualUi, skip);
    }

    [Theory]
    [InlineData(VideoUserDataSaveReason.PlaybackStart)]
    [InlineData(VideoUserDataSaveReason.PlaybackPause)]
    [InlineData(VideoUserDataSaveReason.PlaybackResume)]
    public void LivePlaybackNoiseIsRejected(VideoUserDataSaveReason reason)
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, true, false, Watch, reason), Enabled, 1);
        Assert.Equal(SkipReason.PlaybackNoise, skip);
    }

    [Fact]
    public void ImportsAreRejectedEvenIfVideoReasonLooksLikePlayback()
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, true, true, Watch | EpisodeUserDataSaveReason.Import, VideoUserDataSaveReason.PlaybackEnd), Enabled, 1);
        Assert.Equal(SkipReason.Import, skip);
    }

    [Fact]
    public void ImportFlagWithoutIsImportIsStillRejected()
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, true, false, EpisodeUserDataSaveReason.Import | Watch, VideoUserDataSaveReason.PlaybackEnd), Enabled, 1);
        Assert.Equal(SkipReason.Import, skip);
    }

    [Fact]
    public void UnwatchIsIgnored()
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, false, false, Watch, VideoUserDataSaveReason.PlaybackEnd), Enabled, 1);
        Assert.Equal(SkipReason.Unwatch, skip);
    }

    [Fact]
    public void OtherUsersAreIgnored()
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(99, true, false, Watch, VideoUserDataSaveReason.PlaybackEnd), Enabled, 1);
        Assert.Equal(SkipReason.WrongUser, skip);
    }

    [Fact]
    public void FavoriteAndTagUpdatesAreIgnored()
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, true, false, EpisodeUserDataSaveReason.IsFavorite, VideoUserDataSaveReason.None), Enabled, 1);
        Assert.Equal(SkipReason.NotAWatch, skip);
    }

    [Fact]
    public void DisabledPluginRejectsEverything()
    {
        var skip = ScrobbleGate.Decide(new WatchCandidate(1, true, false, Watch, VideoUserDataSaveReason.PlaybackEnd), new AniListScrobbleOptions { Enabled = false }, 1);
        Assert.Equal(SkipReason.Disabled, skip);
    }
}
