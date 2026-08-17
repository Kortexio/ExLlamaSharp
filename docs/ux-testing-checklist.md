# UX Testing Checklist (Small Business)

Run with a non-technical admin before release.

## 5-minute install
- [ ] Run Check-Requirements.ps1
- [ ] Install service / start `dotnet run --project src/ExLlamaSharp.Server`
- [ ] Open http://127.0.0.1:14563
- [ ] Complete `/setup` wizard (admin, models path, network, optional download)
- [ ] Dashboard shows healthy status

## Models
- [ ] Library cards filter by VRAM
- [ ] Download / import flow shows progress (or clear stub message)
- [ ] Activate model without editing config files

## Team & keys
- [ ] Invite team member from `/team`
- [ ] Create API key from `/keys` with scopes and limits
- [ ] Copy key once and use in `/api` curl example

## Chat & diagnostics
- [ ] `/chat` streams a reply (mock engine OK for CI)
- [ ] Retry button appears on 429/503
- [ ] `/diagnostics` Run Health Check + Test Inference
- [ ] Export support bundle works

## Progressive disclosure
- [ ] Simple dashboard by default
- [ ] Advanced metrics toggle
- [ ] Onboarding tour can be skipped and restarted

## Pass criteria
Admin completes first successful OpenAI request in under 5 minutes without editing JSON/env files.
