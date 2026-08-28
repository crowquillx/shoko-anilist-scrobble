# Source notes

Claims below are from primary sources used while writing the plugin.

## Shoko plugin contract

Daily Shoko plugins target `Shoko.Abstractions` `6.0.0-alpha.77` on `net10.0`
(NuGet package `shoko.abstractions.6.0.0-alpha.77.nupkg`, repository commit
`001dbc0bae76adabaf683b390390e6d935c244a9`). There is no `daily` git branch on
https://github.com/ShokoAnime/ShokoServer; daily builds publish this
abstractions package from current development. Existing plugins in this
workspace (`shoko-simkl-sync`, `shoko-image-planner`) pin the same version.

A plugin implements `IPlugin` plus `IPluginServiceRegistration` /
`IPluginApplicationRegistration`. Watch updates arrive in-process on
`IUserDataService.EpisodeUserDataSaved`. That is the same domain event
`UserDataEventEmitter` publishes on SignalR. A server plugin should not open a
SignalR client back to itself.

`EpisodeUserDataSavedEventArgs` (`Shoko.Abstractions.xml`):

- `VideoReason`: `VideoUserDataSaveReason` (not flags). Values: `None`,
  `UserInteraction`, `PlaybackStart`, `PlaybackPause`, `PlaybackResume`,
  `PlaybackProgress`, `PlaybackEnd`, `Import`.
- `Reason`: `[Flags] EpisodeUserDataSaveReason` (`None`, `Import=1`,
  `LastPlayedAt=2`, `PlaybackCount=4`, `IsFavorite=8`, `UserTags=16`,
  `UserRating=32`).
- `IsImport`, `ImportSource`, `User`, `Episode`, `UserData`.

## Jellyfin vs WebUI

Shokofin (`Development/projects/Shokofin/Shokofin/API/ShokoApiClient.cs`)
patches `/api/v3/File/{fileId}/Scrobble?event=...`. Events: `play`, `pause`,
`resume`, `stop`, `scrobble`, `user-interaction`.

Shoko maps those events onto `VideoUserDataSaveReason` (historical File
controller, commit `e3e31e1` / Trakt restriction commit `805f23d`; the 6.x
enum keeps the same names). Auto-watch at >=97.5% is documented in
`shoko-companion/docs/design/shoko-api.md`.

Shoko WebUI (`Shoko-WebUI/src/core/react-query/episode/mutations.ts`) posts
`Episode/${episodeId}/Watched/${watched}`. That path does not go through
File/Scrobble, so `VideoReason` stays `None`.

## AniList

- GraphQL POST `https://graphql.anilist.co`
  (https://docs.anilist.co/guide/graphql/).
- Mutations need OAuth. Implicit grant + pin redirect
  `https://anilist.co/api/v2/oauth/pin`
  (https://docs.anilist.co/guide/auth/). Tokens last one year. No refresh
  tokens.
- `SaveMediaListEntry(mediaId, progress, status)` 
  (https://docs.anilist.co/reference/mutation). Progress is episodes consumed,
  min 0.
- Rate limit 90/min, currently degraded to 30/min. `Retry-After` and
  `X-RateLimit-*` headers
  (https://anilist.gitbook.io/anilist-apiv2-docs/docs/guide/rate-limiting.md).

## ARM

https://github.com/kawaiioverflow/arm README: JSON rows are
`{ mal_id?, anilist_id?, annict_id?, syobocal_tid? }`. No AniDB field.
Database URL used by this plugin:
`https://raw.githubusercontent.com/kawaiioverflow/arm/master/arm.json`.
Weekly updates.
