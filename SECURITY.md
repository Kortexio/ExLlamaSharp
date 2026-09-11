# Security Policy

## Supported versions

Security fixes are applied to the latest published release on the [Releases](https://github.com/Kortexio/ExLlamaSharp/releases) page (including betas when that is the current shipping channel).

## Reporting a vulnerability

Please **do not** open a public GitHub issue for security-sensitive reports.

Email **security@kortexio.io** (or open a private advisory on GitHub if available) with:

- Affected version / Setup.exe build
- Steps to reproduce
- Impact (auth bypass, RCE, data exposure, etc.)
- Optional patch or mitigation ideas

We aim to acknowledge reports within a few business days.

## Hardening notes for operators

- Change the default admin password and rotate `sk-exllamasharp-dev` before any LAN or production use.
- Prefer binding to localhost unless you intentionally expose the service; use firewall + API keys when serving a team.
- Keep Windows, NVIDIA drivers, and ExLlamaSharp updated.
