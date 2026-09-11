# Docs assets

Images used by the GitHub README and Open Graph preview.

| File | Use |
|------|-----|
| `social-preview.png` | GitHub repo **Social preview** (1280×640). Regenerate: `python generate-social-preview.py` |
| `dashboard.png` | README hero (real Admin Dashboard) |
| `models.png` | Models / Hugging Face library |
| `chat.png` | Chat playground |
| `login.png` | Login page (optional) |

## Recapture UI shots

With the service running (`admin` / your password):

1. `http://127.0.0.1:14563/` → `dashboard.png`
2. `/models` → `models.png`
3. `/chat` → `chat.png`

Then re-run `python generate-social-preview.py` so the OG image picks up the new dashboard crop.

## Wire About on GitHub

See [github-discoverability.md](../github-discoverability.md) or run `scripts/Configure-GitHub-Discoverability.ps1` after `gh auth login`.
