# Codex Authentication

Lis supports OpenAI Codex as an AI provider. Codex uses OAuth 2.0 with PKCE for authentication — there are no static API keys.

## Quick Start

```bash
# 1. Run the auth script (on a machine with a browser)
cd ~/lis/publish
dotnet-script scripts/codex-auth.csx

# 2. Restart the service to load the new tokens
sudo systemctl restart lis
```

The script opens your browser, authenticates with OpenAI, and writes `auth.json` to the current directory.

## Prerequisites

- `dotnet-script` installed: `dotnet tool install -g dotnet-script`
- A ChatGPT Plus/Pro subscription (Codex requires an active subscription)
- Port 1455 available on localhost (used for the OAuth callback)

## How It Works

### Initial Authentication

1. **Run the script** — `dotnet-script scripts/codex-auth.csx`
2. **Browser opens** — navigates to OpenAI's OAuth authorization page
3. **Log in** — authenticate with your OpenAI/ChatGPT account
4. **Callback captured** — browser redirects to `http://localhost:1455/auth/callback`, the script catches the authorization code
5. **Token exchange** — script exchanges the code for access + refresh tokens using PKCE
6. **Tokens saved** — writes to `auth.json` in the current directory

### Token Lifecycle

```
┌─────────────┐     expires      ┌──────────────┐     success     ┌─────────────┐
│    Ready     │ ──────────────> │  Refreshing   │ ─────────────> │    Ready     │
│ (valid JWT)  │                 │ (HTTP in-flight)│               │ (new JWT)    │
└─────────────┘                 └──────────────┘                 └─────────────┘
                                       │ failure
                                       ▼
                                ┌──────────────┐
                                │    Failed     │
                                │ (re-auth needed)│
                                └──────────────┘
```

- Access tokens expire after ~1 hour
- The server automatically refreshes tokens using the refresh token — no manual intervention
- Refreshed tokens are persisted back to `auth.json` so they survive restarts
- If refresh fails (e.g. subscription lapsed), the error is surfaced once in chat and then suppressed until a new `/model` command is issued

### File: `auth.json`

```json
{
  "codex": {
    "access_token": "eyJ...",
    "refresh_token": "rt-...",
    "persisted_at": 1715944800
  }
}
```

Must be in the Lis publish directory (same directory as `Lis.Api.dll`). Loaded at startup, updated after each token refresh.

### Token Priority

1. `auth.json` (persisted OAuth tokens) — preferred
2. `.env` variables (`CODEX_ACCESS_TOKEN`, `CODEX_REFRESH_TOKEN`) — fallback

## Remote Server Setup

When the server is headless (no browser), use SSH port forwarding:

```bash
# From your local machine:
ssh -L 1455:localhost:1455 user@server

# On the server (in the SSH session):
cd ~/lis/publish
dotnet-script scripts/codex-auth.csx

# The browser opens locally, callback reaches the server via the tunnel
```

After auth completes:
```bash
sudo systemctl restart lis
```

## Script Options

```
dotnet-script scripts/codex-auth.csx [options]

Options:
  --output, -o <path>    Write auth.json to a specific path
                         Default: ./auth.json (current directory)

Examples:
  dotnet-script scripts/codex-auth.csx
  dotnet-script scripts/codex-auth.csx --output ~/lis/publish/auth.json
```

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `invalid_state` on OpenAI login page | Stale browser session | Use incognito/private window |
| `Codex stream error` in chat | Tokens not loaded | Ensure `auth.json` is in publish dir, restart service |
| `CodexAuthException` in logs | Refresh token expired | Re-run the auth script |
| Port 1455 already in use | Another process on that port | Kill it: `lsof -ti:1455 \| xargs kill` |
| `Codex is not authenticated` | No tokens configured | Run the auth script |

## OAuth Details

| Parameter | Value |
|---|---|
| Authorization endpoint | `https://auth.openai.com/oauth/authorize` |
| Token endpoint | `https://auth.openai.com/oauth/token` |
| Client ID | `app_EMoamEEZ73f0CkXaXp7hrann` |
| Redirect URI | `http://localhost:1455/auth/callback` |
| Scope | `openid profile email offline_access` |
| PKCE method | S256 |
| Grant type (initial) | `authorization_code` |
| Grant type (refresh) | `refresh_token` |

Account ID is extracted from the access token JWT at claim path `https://api.openai.com/auth` → `chatgpt_account_id`.
