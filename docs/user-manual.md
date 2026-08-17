# ExLlamaSharp User Manual

Non-technical guide for small teams running a local AI chat server on Windows with NVIDIA GPUs.

## What you get

ExLlamaSharp turns a Windows PC with an NVIDIA GPU into a private ChatGPT-style server for your office. People open a browser, pick a model, and chat. Developers can also call the same OpenAI-compatible API.

Default address after install: **http://localhost:14563** (or the server’s LAN IP if networking was enabled in the wizard).

## Install

1. Check the machine meets the basics (NVIDIA GPU with ~8 GB+ VRAM, Windows 10/11, enough free disk). IT can run `packaging/Check-Requirements.ps1`.
2. Install with the ZIP package (`Install.ps1` as Administrator) **or** ask IT to deploy the published server.
3. Open the browser to `http://localhost:14563`.

If the page does not load, wait a minute for the Windows service to start, then open **Diagnostics** (`/diagnostics`) or ask IT to check the service status.

## First-run wizard (about 5 minutes)

On first launch you will be guided through:

1. **Create admin** — username and password for the web UI.
2. **Model folder** — where downloaded models are stored (default under Program Data).
3. **Network** — listen only on this PC, or allow other PCs on the LAN.
4. **Download a starter model** — pick a recommended model and wait for the job to finish.
5. **Done** — you land on the dashboard.

You can change these later under **Settings**.

## Download or import a model

1. Open **Models**.
2. Choose **Pull / download** from the library (Hugging Face style repo id) **or** **Import** a folder you already have.
3. Wait until the job shows completed (Jobs list / progress).
4. Click **Load** so the GPU starts serving that model.

Aliases let you give a short name (for example `office-assistant`) that the Chat page and API use.

Large models need more VRAM and disk. If load fails, try a smaller quantized model or free GPU memory.

## Chat

1. Open **Chat**.
2. Select the loaded model (or alias).
3. Type a message and send.

Tips:

- System prompts and conversation history live in the UI; clear a thread when starting a new topic.
- If replies stop or error, check **Diagnostics** — often “No model loaded”.
- For API use from apps, create a key under **API Keys** and see the in-app API guide.

## Team use

Admins can:

- Create **users** and assign roles.
- Issue **API keys** with quotas / scopes for apps and integrations.
- Enable **teams / tenants** (when multi-tenancy is on) so groups stay isolated.
- Review **audit** activity and set **moderation** rules if needed.

Non-admins usually only need Chat (and maybe a personal API key if your policy allows it).

## Everyday checks

| Need | Where |
|------|--------|
| Is the server healthy? | **Diagnostics** → Run health check |
| GPU / version | **About** |
| Change port or CORS | **Settings → Network** |
| Backup config & keys | **Settings / Backup** (admin) |

## Getting help

1. Open **Diagnostics** and note which component is red/yellow (`database`, `engine`, `inference`, `disk`).
2. See [troubleshooting.md](troubleshooting.md) for the same common issues listed on that page.
3. Admins: [admin-guide.md](admin-guide.md).
