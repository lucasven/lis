# Browser (PinchTab)

Lis drives Chrome through [PinchTab](https://pinchtab.com) — a single Go binary managed entirely by Lis as a sidecar process.

## How it works

- On startup (when `PINCHTAB_ENABLED=true`), Lis downloads the pinned pinchtab binary to `{PINCHTAB_DATA_DIR}/bin` (skipped if already present at the right version), writes `config.json`, and spawns `pinchtab server`.
- The sidecar is supervised: restarted on crash (exponential backoff) and restarted daily at `PINCHTAB_RESTART_HOUR`.
- Each agent gets a persistent browser profile named `agent-{agentId}`. A browser instance starts lazily on the agent's first `browser-navigate` and stops when the conversation turn ends. Profile state (cookies, logins) persists on disk.
- Headless by default. `browser-navigate(headed=true)` opens a visible window when a display is available; in Docker it returns a graceful "not available" message.

## Configuration

| Env var | Default | Description |
|---------|---------|-------------|
| `PINCHTAB_ENABLED` | `false` | Enable the browser plugin + sidecar |
| `PINCHTAB_PORT` | `9867` | Orchestrator HTTP port |
| `PINCHTAB_VERSION` | `v0.14.0` | Pinned release version |
| `PINCHTAB_DATA_DIR` | `./data/pinchtab` | Binary, config, profiles, and state |
| `PINCHTAB_CHROME_PATH` | (auto-detect) | Chrome/Chromium binary path |
| `PINCHTAB_RESTART_HOUR` | `4` | Local hour for the daily scheduled restart |
| `PINCHTAB_TOKEN` | (generated) | API auth token; random per boot when empty |

## Bare metal

Chrome or Chromium must be installed. PinchTab auto-detects common locations; set `PINCHTAB_CHROME_PATH` to override. The pinchtab binary is downloaded automatically on first start.

## Docker

The image bakes in both Chromium (`apt`) and the pinchtab binary (release download), so no runtime downloads happen:

- `PINCHTAB_DATA_DIR=/opt/pinchtab`
- `PINCHTAB_CHROME_PATH=/usr/bin/chromium`

Headed mode is unavailable in Docker (no display).

## Troubleshooting

- **Browser tools missing**: `PINCHTAB_ENABLED` is not `true`.
- **"Headed mode is not available"**: no `DISPLAY`/`WAYLAND_DISPLAY` (Linux). Use headless.
- **Sidecar won't start**: check logs for `pinchtab` lines; verify the binary is executable and Chrome is found.
- **Reset all browser state**: stop Lis and delete `{PINCHTAB_DATA_DIR}/profiles`.
