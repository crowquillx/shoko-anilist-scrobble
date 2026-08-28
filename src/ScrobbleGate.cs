using Shoko.Abstractions.User.Enums;

namespace Shoko.AniListScrobble;

public enum SkipReason
{
    None,
    Disabled,
    Unwatch,
    Import,
    WrongUser,
    ManualUi,
    PlaybackNoise,
    NotAWatch,
    MissingEpisode,
    Hidden,
    MissingSeries,
    MissingAniDb,
    UnsupportedEpisodeType,
    InvalidEpisodeNumber,
    MissingAnilist,
    AlreadyScrobbled,
    AlreadyAhead,
}

public sealed record WatchCandidate(
    int UserId,
    bool IsWatched,
    bool IsImport,
    EpisodeUserDataSaveReason Reason,
    VideoUserDataSaveReason VideoReason);

public static class ScrobbleGate
{
    public static readonly EpisodeUserDataSaveReason WatchFlags =
        EpisodeUserDataSaveReason.LastPlayedAt | EpisodeUserDataSaveReason.PlaybackCount;

    public static SkipReason Decide(WatchCandidate candidate, AniListScrobbleOptions options, int? configuredUserId)
    {
        if (!options.Enabled)
            return SkipReason.Disabled;
        if (!candidate.IsWatched)
            return SkipReason.Unwatch;
        if (candidate.IsImport || candidate.Reason.HasFlag(EpisodeUserDataSaveReason.Import))
            return SkipReason.Import;
        if (configuredUserId is int userId && candidate.UserId != userId)
            return SkipReason.WrongUser;
        if ((candidate.Reason & WatchFlags) == 0 && candidate.VideoReason is VideoUserDataSaveReason.None)
            return SkipReason.NotAWatch;

        return candidate.VideoReason switch
        {
            VideoUserDataSaveReason.PlaybackEnd => SkipReason.None,
            VideoUserDataSaveReason.PlaybackProgress => SkipReason.None,
            VideoUserDataSaveReason.UserInteraction when options.AcceptJellyfinToggles => SkipReason.None,
            VideoUserDataSaveReason.UserInteraction => SkipReason.ManualUi,
            VideoUserDataSaveReason.PlaybackStart or VideoUserDataSaveReason.PlaybackPause or VideoUserDataSaveReason.PlaybackResume
                => SkipReason.PlaybackNoise,
            _ => SkipReason.ManualUi,
        };
    }
}
