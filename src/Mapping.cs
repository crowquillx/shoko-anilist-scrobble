using System.Collections;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anilist.CrossReferences;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.User;

namespace Shoko.AniListScrobble;

public enum MappingSource
{
    None,
    ShokoEpisode,
    ShokoSeries,
    ArmMal,
}

public sealed record MappedWatch(
    int UserId,
    int EpisodeId,
    int AnidbAnimeId,
    int AnilistId,
    int ProgressIndex,
    bool IsMovie,
    MappingSource Source,
    IReadOnlyList<int> MalIds);

public static class EpisodeMapper
{
    public static bool TryMap(
        IShokoEpisode? episode,
        IUser? user,
        IEpisodeUserData? userData,
        IArmCatalog arm,
        out MappedWatch mapped,
        out SkipReason skip)
    {
        mapped = null!;
        if (episode is null)
        {
            skip = SkipReason.MissingEpisode;
            return false;
        }

        if (episode.IsHidden)
        {
            skip = SkipReason.Hidden;
            return false;
        }

        var series = episode.Series ?? episode.AnidbEpisode?.Series?.ShokoSeries.FirstOrDefault();
        if (series is null)
        {
            skip = SkipReason.MissingSeries;
            return false;
        }

        var anidbId = series.AnidbAnimeID;
        if (anidbId <= 0)
        {
            skip = SkipReason.MissingAniDb;
            return false;
        }

        var isMovie = series.Type is AnimeType.Movie;
        if (!isMovie && episode.Type is not EpisodeType.Episode)
        {
            skip = SkipReason.UnsupportedEpisodeType;
            return false;
        }

        var xref = BestEpisodeXref(episode);
        var progress = xref is { EpisodeNumber: > 0 }
            ? xref.EpisodeNumber
            : isMovie ? 1 : episode.EpisodeNumber;
        if (progress <= 0)
        {
            skip = SkipReason.InvalidEpisodeNumber;
            return false;
        }

        var malIds = CollectInts(series.AnidbAnime?.MalIDs);
        int anilistId;
        MappingSource source;
        if (xref is { AnilistAnimeID: > 0 })
        {
            anilistId = xref.AnilistAnimeID;
            source = MappingSource.ShokoEpisode;
        }
        else if (CollectMetadataIds(series.AnilistAnime) is { Count: > 0 } seriesIds)
        {
            anilistId = seriesIds[0];
            source = MappingSource.ShokoSeries;
        }
        else if (arm.TryResolve(malIds, out var armId))
        {
            anilistId = armId;
            source = MappingSource.ArmMal;
        }
        else
        {
            skip = SkipReason.MissingAnilist;
            return false;
        }

        skip = SkipReason.None;
        mapped = new MappedWatch(
            UserId: MetadataId(user) ?? userData?.UserID ?? 0,
            EpisodeId: MetadataId(episode) ?? userData?.EpisodeID ?? 0,
            AnidbAnimeId: anidbId,
            AnilistId: anilistId,
            ProgressIndex: progress,
            IsMovie: isMovie,
            Source: source,
            MalIds: malIds);
        return true;
    }

    public static string ScrobbledKey(int userId, int episodeId) => $"{userId}:{episodeId}";

    public static int? MetadataId(object? entity)
    {
        if (entity is IMetadata<int> metadata && metadata.ID > 0)
            return metadata.ID;
        return null;
    }

    public static IAnilistEpisodeCrossReference? BestEpisodeXref(IShokoEpisode episode)
    {
        IAnilistEpisodeCrossReference? best = null;
        foreach (var xref in episode.AnilistEpisodeCrossReferences ?? [])
        {
            if (xref is null || xref.AnilistAnimeID <= 0 || xref.EpisodeNumber <= 0)
                continue;
            if (best is null || Rank(xref.MatchRating) < Rank(best.MatchRating))
                best = xref;
        }

        return best;
    }

    public static IReadOnlyList<int> CollectInts(object? value)
    {
        if (value is null)
            return [];
        if (value is IEnumerable<int> ints)
            return ints.Where(id => id > 0).Distinct().ToArray();

        var ids = new List<int>();
        if (value is IEnumerable items)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case int id when id > 0:
                        ids.Add(id);
                        break;
                    case IMetadata<int> metadata when metadata.ID > 0:
                        ids.Add(metadata.ID);
                        break;
                    default:
                        if (int.TryParse(item?.ToString(), out var parsed) && parsed > 0)
                            ids.Add(parsed);
                        break;
                }
            }
        }

        return ids.Distinct().ToArray();
    }

    public static IReadOnlyList<int> CollectMetadataIds(IEnumerable? items)
    {
        if (items is null)
            return [];
        var ids = new List<int>();
        foreach (var item in items)
        {
            if (item is IMetadata<int> metadata && metadata.ID > 0)
                ids.Add(metadata.ID);
        }

        return ids.Distinct().ToArray();
    }

    private static int Rank(MatchRating rating)
        => rating is MatchRating.None ? int.MaxValue : (int)rating;
}
