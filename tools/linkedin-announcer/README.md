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

Post format: short hook + summary + CTA. Optional `LINKEDIN_ORG_URN` posts as the company page.
