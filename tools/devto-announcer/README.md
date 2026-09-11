# DevToAnnouncer

Publishes a release article to [dev.to](https://dev.to) via API key.

```bash
export DRY_RUN=true
export RELEASE_TAG=v1.2.1-beta
export RELEASE_BODY="- feat: ..."
export REPO_URL=https://github.com/Kortexio/ExLlamaSharp
export DEVTO_API_KEY=...
dotnet run --project tools/devto-announcer -- post
```

## Skip on release

Put `[skip-announce]` in the GitHub Release notes body to publish a release without posting to LinkedIn or dev.to.
