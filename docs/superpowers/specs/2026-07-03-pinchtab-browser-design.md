# PinchTab Browser Integration — Design

**Date:** 2026-07-03
**Status:** Approved

## Goal

Replace the Playwright-based `BrowserPlugin` (+ `BrowserSessionManager`) and the `web-fetch` tool with [PinchTab](https://pinchtab.com) — a standalone Go binary that drives Chrome and exposes an HTTP API. Lis fully owns the sidecar: downloading the binary, running the server, supervising it, and managing per-agent browser instances. The Brave `web-search` tool stays unchanged.

## Requirements

- Remove Playwright dependency, `BrowserSessionManager`, and `WebPlugin.FetchAsync`; keep `WebPlugin.SearchAsync` (Brave).
- Lis downloads a pinned pinchtab release to a data dir and manages it as a child process.
- PinchTab server is always on (starts with Lis), restarts on crash, and is gracefully restarted once a day at a configured hour.
- Each agent gets its own pinchtab profile (`agent-{agentId}`); instances are profile-backed, started lazily on first browser tool call. Headless by default, with opt-in headed mode when a display is available (graceful "not available" response in Docker).
- Instances close automatically when the conversation turn ends (`ConversationService.RespondAsync` completes). Profiles persist on disk, so cookies/logins survive across turns and server restarts.
- Pinchtab-native tool surface: accessibility-tree snapshots with element refs (`e0`, `e1`, ...) — no explicit `start`/`close` tools.
- Must work in Docker (Chromium + pinchtab baked into the image) and bare metal (runtime binary download, Chrome auto-detection).

## Architecture

All new code lives in `Lis.Tools/Pinchtab/`, replacing `Lis.Tools/Browser/`:

```
Lis.Tools/Pinchtab/
├── PinchtabOptions.cs           — env-backed options
├── PinchtabBinaryProvisioner.cs — downloads pinned release to data dir if missing/outdated
├── PinchtabProcessManager.cs    — BackgroundService: provision → spawn `pinchtab server` →
│                                  health-check loop, crash restart w/ backoff, daily restart
├── PinchtabClient.cs            — typed HttpClient over the REST API
└── PinchtabInstanceManager.cs   — agentId → profile/instance mapping, lazy start,
                                   CloseForAgentAsync
Lis.Tools/BrowserPlugin.cs       — rewritten on PinchtabClient/InstanceManager
                                   (plugin name "browser" unchanged)
```

**Removed:** `Lis.Tools/Browser/BrowserSessionManager.cs`, `Microsoft.Playwright` package reference, Playwright layers in `Lis.Api/Dockerfile`, `web-fetch` tool.

### Process supervision (PinchtabProcessManager)

`BackgroundService` registered in `AgentSetup` (conditional on `PINCHTAB_ENABLED=true`):

1. **Start:** provision binary (via `PinchtabBinaryProvisioner`) → spawn `pinchtab server` child process → poll `/health` until ready.
2. **Loop:** if the process exits unexpectedly, restart with exponential backoff. Once a day at `PINCHTAB_RESTART_HOUR`, gracefully stop and restart the server.
3. **Shutdown:** kill the child process.

Agent instances are lazy, so a crashed/restarted server just means the next browser call creates a fresh instance. Profiles persist on disk.

### Instance lifecycle (PinchtabInstanceManager)

- `EnsureInstanceAsync(agentId, headed = false)`: if no live instance for the agent, `POST /instances/start` with profile `agent-{agentId}` (headless unless headed requested and a display is available); subsequent requests route to that instance. Recreates transparently if the instance died mid-turn.
- `CloseForAgentAsync(agentId)`: stops the instance (profile kept).
- **Turn-end hook:** `ConversationService.RespondAsync` (Lis.Agent) gets a `finally` calling `CloseForAgentAsync`. The same cleanup applies to subagent runs.

## Tool Surface

New `BrowserPlugin` (plugin name `browser`, all tools `OwnerOnly`, string-error style as today):

| Tool | Pinchtab call | Notes |
|---|---|---|
| `navigate(url, headed?)` | `POST /navigate` | Auto-starts the agent's instance on first use; `headed=true` opens a visible Chrome window when a display is available |
| `snapshot(filter?)` | `GET /snapshot?filter=interactive` | Aria tree with refs; `Summarize` policy, length-capped |
| `click(ref)` | `POST /action {kind:"click"}` | |
| `type(ref, text)` | `POST /action {kind:"fill"}` | |
| `press(ref?, key)` | `POST /action {kind:"press"}` | Enter, Tab, etc. |
| `scroll(direction)` | `POST /action {kind:"scroll"}` | |
| `screenshot()` | `GET /screenshot` | Returns `data:image/png;base64,...` |
| `eval(code)` | `POST /eval` | JS in page context |
| `tabs()` | `GET /tabs` | |

Tool descriptions tell the model there is no start/close — just navigate. Every tool goes through `EnsureInstanceAsync` first.

### Headed mode

Headless is the default. `navigate(headed: true)` starts the agent's instance in headed mode — useful on bare metal so the owner can watch or manually log into sites, seeding the persistent profile. If no display is available (Docker/headless host, detected via `DISPLAY` on Linux; macOS assumed available), the tool returns a graceful "headed mode not available in this environment — use headless" message instead of starting the instance. The mode applies at instance start: requesting a different mode while an instance is running restarts that instance (profile persists).

### Error handling

- PinchTab unreachable → tool returns "browser backend restarting, retry shortly" (process manager handles the actual restart); no exceptions surfaced to the agent loop.
- Instance dead mid-turn → recreated transparently by `EnsureInstanceAsync`.
- Chrome not found (bare metal) → provisioner/process manager logs a clear error; tools report browser unavailable with the reason.

## Configuration

Flat env vars (added to `.env.example`):

```
PINCHTAB_ENABLED=true
PINCHTAB_PORT=9867
PINCHTAB_VERSION=              # exact release tag, pinned at implementation time
                               # from the latest github.com/pinchtab/pinchtab release
PINCHTAB_DATA_DIR=./data/pinchtab   # binary + profiles
PINCHTAB_CHROME_PATH=               # optional override; else auto-detect
PINCHTAB_RESTART_HOUR=4             # daily restart hour (local time)
```

`BrowserPlugin` registration and the hosted service are conditional on `PINCHTAB_ENABLED=true`, same pattern as providers/channels. `PinchtabOptions` wraps the env vars via `Options.Create()`.

## Deployment

- **Docker:** `Lis.Api/Dockerfile` drops the Playwright/pwsh layers and Playwright browser copy; adds `chromium` apt package and downloads the pinned pinchtab binary into `PINCHTAB_DATA_DIR` at build time (provisioner sees it present and skips download).
- **Bare metal:** provisioner downloads the binary at startup; Chrome detected in standard locations per platform, clear error if absent.

## Testing

- Unit tests for `PinchtabInstanceManager` (mapping, lazy start, close, recreate-on-dead) against a faked client.
- Unit tests for `PinchtabBinaryProvisioner` (skip-if-present, version pinning).
- `PinchtabClient` and `BrowserPlugin` are thin HTTP wrappers — verified manually end-to-end.

## Out of Scope

- Replacing `web-search` (Brave stays).
- Instance idle timeouts, multi-instance per agent, backend abstraction (`IBrowserBackend`).
