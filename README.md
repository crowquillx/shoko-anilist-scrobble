# Shoko AniList Scrobble

Scrobbles Jellyfin watches to AniList. It is not a library sync. History already
in Shoko stays off AniList. Marks you make in the Shoko WebUI stay off AniList.

Watch an episode in Jellyfin. Shokofin tells Shoko. This plugin hears that save
and sets AniList progress to that episode index. When the index reaches the last
episode, AniList gets `COMPLETED`.

## Install from Shoko

Daily Shoko only (`net10.0`, `Shoko.Abstractions` 6.x). Stable 5.3 will not load
this.

Shoko's plugin manager wants a metadata feed with a text content type. A GitHub
Release asset comes back as `application/octet-stream` and Shoko rejects it.
Use the raw URL on the `metadata` branch.

1. Open Settings → Plugins → Repositories.
2. Add this repository URL:

```
https://raw.githubusercontent.com/crowquillx/shoko-anilist-scrobble/metadata/manifest.json
```

3. Sync the repository.
4. Install **Shoko AniList Scrobble**.
5. Restart Shoko.

Then connect AniList on the plugin page. Create an API client at
[anilist.co/settings/developer](https://anilist.co/settings/developer) with
redirect URL `https://anilist.co/api/v2/oauth/pin`. Put the client ID in plugin
settings, restart again, authorize, paste the token.

Tokens last a year. They live in the plugin state file, not in logs, status
JSON, or the WebUI page source.

Settings are documented in [`docs/configuration.md`](docs/configuration.md).
Cutting a release is documented in [`docs/releases.md`](docs/releases.md).

## What gets scrobbled

Regular AniDB episodes (`EpisodeType.Episode`) when Shokofin saves with
`PlaybackEnd` or `PlaybackProgress` (auto-watch at 97.5%). Jellyfin watched
toggles (`UserInteraction`) are on by default. Movies go out as progress 1,
then `COMPLETED`.

Specials, credits, trailers, parodies, and "other" episode types are skipped.
Unwatch is ignored. Imports are ignored. Favorite, tag, and rating updates are
ignored.

AniList progress is an index, not a bitset. Watching episode 7 sets progress to
7. If AniList already has a higher index, the write is skipped. A completed
title is not reopened.

## Jellyfin vs WebUI

Shoko stamps every episode user-data save with `VideoUserDataSaveReason`.
Shokofin maps File/Scrobble events like this:

| Shokofin `event` | `VideoUserDataSaveReason` |
| --- | --- |
| `stop` | `PlaybackEnd` |
| `scrobble` | `PlaybackProgress` |
| `user-interaction` | `UserInteraction` |
| `play` / `pause` / `resume` | ignored here |

The WebUI posts `Episode/{id}/Watched/{watched}`. That save has
`VideoReason = None`, so it never reaches AniList.

## Mapping

Resolution order:

1. Shoko episode-level AniList cross-reference (handles AniList season splits)
2. Shoko series-level AniList IDs
3. [arm-server](https://github.com/BeeeQueue/arm-server) by AniDB ID
4. arm-server by Shoko's MAL IDs, only if the AniDB lookup misses

kawaiioverflow/arm has no AniDB field. Shoko's MAL IDs are often missing or
wrong. AniDB is what Shoko actually has. The plugin calls
`GET /api/v2/ids?source=anidb&id={id}&include=anilist` on
https://arm.haglund.dev and caches the result under Shoko's data directory
for `ArmCacheHours` (default one week). MAL is the last hop, through the same
API (`source=myanimelist`).

## API

Admin-only routes under `/api/v3/Plugin/AniListScrobble`. There is no export
endpoint.

- `GET status`
- `GET auth/authorize-url`
- `POST auth/token`
- `POST auth/disconnect`

The WebUI page is a same-origin embed.

## Build

Use a local or Nix-provided .NET 10 SDK:

```sh
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
bash build/package.sh Release
```

## License

MIT. See [`LICENSE`](LICENSE).
