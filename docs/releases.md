# Releases

Tagged pushes build the plugin zip, put it on a GitHub Release, and update the
`metadata` branch that Shoko's plugin manager reads.

## Install URL

In Shoko, Settings → Plugins → Repositories, add:

```
https://raw.githubusercontent.com/crowquillx/shoko-anilist-scrobble/metadata/manifest.json
```

That raw URL returns `text/plain`. The GitHub Releases `manifest.json` asset
returns `application/octet-stream`, which Shoko will not parse as a feed.

Each release entry in the feed points at that tag's zip. Shoko checks the
`sha256:` checksum and uses `channel` to pick Stable vs Dev.

A fork must use its own raw URL,
`https://raw.githubusercontent.com/OWNER/REPO/metadata/manifest.json`.
The workflows derive `repository_url` from `github.repository`, so a fork's
feed points at the fork.

## Cut a release

Stable tags are `vMAJOR.MINOR.PATCH`. Dev tags are `vMAJOR.MINOR.PATCH-dev.N`.
Anything else fails the workflow.

```sh
git tag -a v1.0.0 -m "Shoko AniList Scrobble 1.0.0"
git push origin v1.0.0
```

The Release workflow restores, tests, packages `Shoko.AniListScrobble-<version>-any.zip`,
uploads the zip plus checksum plus `manifest.json`, then merges that one release
into `metadata/manifest.json`. Same-tag retries replace the old entry. Do not
force-push the metadata branch.

## Local packaging

```sh
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
bash build/package.sh Release
bash build/verify-package.sh artifacts/Shoko.AniListScrobble-1.0.0-any.zip artifacts/manifest.json
```

`build/package.sh` accepts `REPOSITORY_URL`, `HOMEPAGE_URL`, `TAG`,
`SOURCE_REVISION`, `RELEASED_AT`, and `RELEASE_NOTES_FILE` so a local run can
match CI.
