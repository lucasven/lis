# Browser Handoff — Design

**Date:** 2026-07-07
**Status:** Approved

## Goal

Let the agent hand its headed browser to the user for manual interaction (login, captcha, 2FA, checkout) with the agent's session/cookies intact. Today `PinchtabInstanceManager` closes the agent's browser instance the moment the conversation turn ends, so a headed window never survives long enough for the user to touch it.

## Requirements

- Agent-initiated: a new `browser-handoff` tool marks the current headed session as handed off. The agent then finishes its reply telling the user what to do.
- The handed-off instance survives turn end. The window stays on screen with the same tabs.
- Handoff ends on whichever comes first:
  - The user's next message to that agent (`BeginTurn`) — the agent reclaims the same instance and continues with the user's fresh cookies.
  - A timeout (`PINCHTAB_HANDOFF_TIMEOUT_MINUTES`, default 15) — the instance is closed and state cleared.
- Headless sessions cannot be handed off; the tool returns an instructive error ("open the page with headed=true first").
- Headless lifecycle is unchanged: instances still close at turn end.

## Design

All changes live in `Lis.Tools` (plus one option + `.env.example` line). No new pinchtab API surface — between turns no automation can touch the instance, so pausing automation is implicit (pinchtab's native tab-handoff API is intentionally not used).

### Tool surface (`BrowserPlugin`)

New kernel function `handoff` (`browser-handoff`, `OwnerOnly`, string-error style):

- Description: "Hand the browser to the user for manual interaction (login, captcha, 2FA). The browser window stays open after your reply; the user's next message returns control to you."
- Requires an existing **headed** session; otherwise returns the instructive error above.
- On success calls `PinchtabInstanceManager.MarkHandoff(agentId)` and returns "Browser handed off — finish your reply telling the user what to do."
- Calling it twice in a turn is idempotent.

### State & lifecycle (`PinchtabInstanceManager`)

- `AgentBrowserSession` gains `HandedOff` (bool, settable).
- `MarkHandoff(agentId)`: sets the flag on the agent's live session; error result if no session or session is headless.
- `CloseForAgentAsync` (reached from `EndTurnAsync` when the last concurrent turn ends): if the session is `HandedOff`, skip `StopInstanceAsync`; arm a per-agent handoff timer (`CancellationTokenSource` + `Task.Delay`). On expiry: close the instance, clear session and flag, log.
- `BeginTurn(agentId)`: if a handoff timer is pending, cancel it and clear `HandedOff`. The session is reclaimed as-is (same instance/tabs/cookies). Normal lifecycle resumes — the instance closes at this turn's end unless the agent hands off again.
- Turn reference counting is unchanged; handoff only alters the "last turn out closes the instance" step.

### Configuration

- `PinchtabOptions.HandoffTimeoutMinutes` (int, default 15) from `PINCHTAB_HANDOFF_TIMEOUT_MINUTES`; documented in `.env.example`.

## Edge cases

- **Mode change on reclaim:** if the next turn requests headless, the existing mode-mismatch path stops and restarts the instance; cookies survive via the on-disk profile. No new code.
- **Timer fires while a turn is active** (user replied just before expiry): the timer callback checks `activeTurns` under the existing lock and no-ops when a turn is running.
- **App shutdown:** `Dispose` cancels outstanding handoff timers; sidecar shutdown closes instances regardless.
- **Instance dies during handoff:** next turn's `EnsureSessionAsync` already drops dead sessions and starts fresh.

## Testing

Extend `PinchtabInstanceManagerTests` (existing fake client):

- Handoff skips close at turn end.
- Next `BeginTurn` reclaims (no restart) and that turn's end closes the instance.
- Timeout closes the instance (short/injectable timeout).
- Headless session cannot hand off.
- Double handoff in one turn is idempotent.
