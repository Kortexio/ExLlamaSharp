# LinkedInAnnouncer

.NET 9 console tool that posts ExLlamaSharp release announcements to LinkedIn.

## Modes

```bash
# One-shot OAuth (local)
export LINKEDIN_CLIENT_ID=...
export LINKEDIN_CLIENT_SECRET=...
dotnet run --project tools/linkedin-announcer -- get-token

# CI / local post
export DRY_RUN=true
export RELEASE_TAG=v1.2.1-beta
export RELEASE_BODY="- feat: ..."
export REPO_URL=https://github.com/Kortexio/ExLlamaSharp
# + LINKEDIN_* secrets
dotnet run --project tools/linkedin-announcer -- post
```

Post format: hook + up to 6 improvement bullets from the release body + repo URL + Setup.exe download URL + release notes URL + hashtags.

Optional `LINKEDIN_ORG_URN` posts as the company page.

## Skip on release

The workflow `.github/workflows/announce-release.yml` runs on every published GitHub Release.
To ship a release **without** LinkedIn/dev.to posts, put this in the release notes body:

```text
[skip-announce]
```
