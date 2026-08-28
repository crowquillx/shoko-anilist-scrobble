# Configuration

Open the AniList Scrobble page in the Shoko WebUI. It loads settings with
`GET /api/v3/Configuration?pluginID=a3e8c1f0-4b7d-4e9a-9c2f-6d1b8a5e3f70` and
saves them with Shoko's Configuration API.

Settings require a restart. Saving an AniList token writes the plugin state
file and takes effect immediately.

Settings:

- `Enabled`: master switch, default `true`.
- `ClientId`: AniList API client ID.
- `ShokoUserId`: Shoko user whose Jellyfin watches are scrobbled. `0` selects
  the first administrator.
- `AcceptJellyfinToggles`: scrobble Shokofin `user-interaction` watched
  toggles, default `true`. WebUI marks are still ignored.
- `RequestTimeoutSeconds`: HTTP timeout, default `20`.
- `MaxJsonResponseBytes`: bounded AniList JSON body, default `1048576`.
- `ArmCacheHours`: ARM catalog cache lifetime, default `168` (one week).

The access token is stored in `<DataPath>/shoko-anilist-scrobble-state.json`
with owner-only permissions when the OS supports them. Status, logs, and the
embedded UI never include the token. The client ID lives in Shoko
configuration and is shown as a blank password field on the plugin page.
