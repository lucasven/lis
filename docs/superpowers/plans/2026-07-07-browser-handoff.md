# Browser Handoff Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the agent hand its headed browser to the user (login/captcha/2FA) so the instance survives past turn end, reclaimed on the user's next message or closed on timeout.

**Architecture:** All state lives in `PinchtabInstanceManager` (Lis.Tools). A `HandedOff` flag on `AgentBrowserSession` makes `CloseForAgentAsync` skip the stop and arm a timeout timer; `BeginTurn` cancels the timer and reclaims the session. A new `browser-handoff` kernel function sets the flag. Spec: `docs/superpowers/specs/2026-07-07-browser-handoff-design.md`.

**Tech Stack:** .NET 10, xUnit, Semantic Kernel plugin conventions already used in `Lis.Tools`.

## Global Constraints

- Tabs for indentation, LF endings, `this.` prefix, aligned `=` in blocks, `is null`/`is not null` (see CLAUDE.md Code Style).
- Same-line `if (x) return y;` for guard clauses.
- `[Trace("Class > Method")]` on public async methods.
- Logging via `[LoggerMessage]` partial methods (class is already `partial`).
- Run tests with: `dotnet test Lis.Tests/Lis.Tests.csproj`
- After all code changes: `DOTNET_ROOT=$HOME/.dotnet ~/.dotnet/tools/jb cleanupcode Lis.Tools/Lis.Tools.csproj --profile="Built-in: Full Cleanup" --settings=Lis.sln.DotSettings` (from repo root), then verify the diff touched only intended files.
- Never use `dotnet format`.
- Commits: gitmoji + conventional commits.

---

### Task 1: `PINCHTAB_HANDOFF_TIMEOUT_MINUTES` option

**Files:**
- Modify: `Lis.Tools/Pinchtab/PinchtabOptions.cs`
- Modify: `Lis.Tests/PinchtabOptionsTests.cs`
- Modify: `.env.example` (after the `PINCHTAB_TOKEN` line, ~line 76)

**Interfaces:**
- Produces: `PinchtabOptions.HandoffTimeoutMinutes` (int, default 15) — Task 2 reads it in the `PinchtabInstanceManager` constructor.

- [ ] **Step 1: Extend both existing options tests (failing first)**

In `Lis.Tests/PinchtabOptionsTests.cs`, add to `Defaults_are_applied_when_env_is_empty` — a reset line next to the other resets, and an assertion next to the other asserts:

```csharp
Environment.SetEnvironmentVariable("PINCHTAB_HANDOFF_TIMEOUT_MINUTES", null);
// ...
Assert.Equal(15, options.HandoffTimeoutMinutes);
```

Add to `Env_values_override_defaults` — a set line and an assertion:

```csharp
Environment.SetEnvironmentVariable("PINCHTAB_HANDOFF_TIMEOUT_MINUTES", "30");
// ...
Assert.Equal(30, options.HandoffTimeoutMinutes);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter PinchtabOptionsTests`
Expected: compile error — `PinchtabOptions` has no `HandoffTimeoutMinutes` (build failure counts as the failing state).

- [ ] **Step 3: Implement the option**

In `Lis.Tools/Pinchtab/PinchtabOptions.cs`, add the property after `RestartHour`:

```csharp
public int     RestartHour { get; init; } = 4;
public int     HandoffTimeoutMinutes { get; init; } = 15;
```

And in `FromEnvironment()`, after the `RestartHour` line:

```csharp
RestartHour = int.TryParse(Env("PINCHTAB_RESTART_HOUR"), out int hour) ? hour : 4,
HandoffTimeoutMinutes = int.TryParse(Env("PINCHTAB_HANDOFF_TIMEOUT_MINUTES"), out int handoff) ? handoff : 15,
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter PinchtabOptionsTests`
Expected: PASS (all PinchtabOptionsTests).

- [ ] **Step 5: Document the env var**

In `.env.example`, after `PINCHTAB_TOKEN=`:

```bash
PINCHTAB_HANDOFF_TIMEOUT_MINUTES=15         # close a handed-off headed browser after this many idle minutes
```

- [ ] **Step 6: Commit**

```bash
git add Lis.Tools/Pinchtab/PinchtabOptions.cs Lis.Tests/PinchtabOptionsTests.cs .env.example
git commit -m "✨ feat(browser): add PINCHTAB_HANDOFF_TIMEOUT_MINUTES option"
```

---

### Task 2: Handoff lifecycle in `PinchtabInstanceManager`

**Files:**
- Modify: `Lis.Tools/Pinchtab/PinchtabInstanceManager.cs`
- Modify: `Lis.Tests/PinchtabInstanceManagerTests.cs`

**Interfaces:**
- Consumes: `PinchtabOptions.HandoffTimeoutMinutes` (Task 1).
- Produces: `PinchtabInstanceManager.MarkHandoff(long agentId): string` (returns the tool-facing message, success or error) — Task 3 calls it. Constructor becomes `PinchtabInstanceManager(IPinchtabClient client, PinchtabOptions options, ILogger<PinchtabInstanceManager> logger)`. `AgentBrowserSession.HandedOff` (bool, get/set). `PinchtabInstanceManager.HandoffTimeout` (TimeSpan, get/set — test seam, defaults to options value).

- [ ] **Step 1: Update the test helper for the new constructor and write failing tests**

In `Lis.Tests/PinchtabInstanceManagerTests.cs`, replace `CreateManager`:

```csharp
private static PinchtabInstanceManager CreateManager(FakePinchtabClient client) =>
	new(client, new PinchtabOptions(), NullLogger<PinchtabInstanceManager>.Instance);
```

Append these tests inside the class:

```csharp
[Fact]
public async Task Handoff_skips_close_at_turn_end() {
	FakePinchtabClient client = new();
	PinchtabInstanceManager manager = CreateManager(client);

	manager.BeginTurn(7);
	AgentBrowserSession session = await manager.EnsureSessionAsync(7, headed: true, CancellationToken.None);
	string result = manager.MarkHandoff(7);
	await manager.EndTurnAsync(7);

	Assert.Contains("handed off", result);
	Assert.Empty(client.StoppedInstances);
	Assert.NotNull(manager.GetSession(7));
	Assert.Equal(session.InstanceId, manager.GetSession(7)!.InstanceId);
}

[Fact]
public async Task BeginTurn_reclaims_handoff_and_next_turn_end_closes() {
	FakePinchtabClient client = new();
	PinchtabInstanceManager manager = CreateManager(client);

	manager.BeginTurn(7);
	AgentBrowserSession session = await manager.EnsureSessionAsync(7, headed: true, CancellationToken.None);
	manager.MarkHandoff(7);
	await manager.EndTurnAsync(7);

	manager.BeginTurn(7);
	Assert.False(manager.GetSession(7)!.HandedOff);
	await manager.EndTurnAsync(7);

	Assert.Contains(session.InstanceId, client.StoppedInstances);
	Assert.Null(manager.GetSession(7));
}

[Fact]
public async Task Handoff_timeout_closes_instance() {
	FakePinchtabClient client = new();
	PinchtabInstanceManager manager = CreateManager(client);
	manager.HandoffTimeout = TimeSpan.FromMilliseconds(50);

	manager.BeginTurn(7);
	AgentBrowserSession session = await manager.EnsureSessionAsync(7, headed: true, CancellationToken.None);
	manager.MarkHandoff(7);
	await manager.EndTurnAsync(7);
	Assert.Empty(client.StoppedInstances);

	for (int i = 0; i < 40 && client.StoppedInstances.Count == 0; i++) await Task.Delay(50);

	Assert.Contains(session.InstanceId, client.StoppedInstances);
	Assert.Null(manager.GetSession(7));
}

[Fact]
public async Task MarkHandoff_rejects_headless_session() {
	FakePinchtabClient client = new();
	PinchtabInstanceManager manager = CreateManager(client);

	manager.BeginTurn(7);
	await manager.EnsureSessionAsync(7, headed: false, CancellationToken.None);
	string result = manager.MarkHandoff(7);
	await manager.EndTurnAsync(7);

	Assert.Contains("headless", result);
	Assert.NotEmpty(client.StoppedInstances);
}

[Fact]
public void MarkHandoff_without_session_returns_error() {
	FakePinchtabClient client = new();
	PinchtabInstanceManager manager = CreateManager(client);

	string result = manager.MarkHandoff(7);

	Assert.Contains("No browser session", result);
}

[Fact]
public async Task Double_handoff_is_idempotent() {
	FakePinchtabClient client = new();
	PinchtabInstanceManager manager = CreateManager(client);

	manager.BeginTurn(7);
	await manager.EnsureSessionAsync(7, headed: true, CancellationToken.None);
	manager.MarkHandoff(7);
	string second = manager.MarkHandoff(7);
	await manager.EndTurnAsync(7);

	Assert.Contains("handed off", second);
	Assert.Empty(client.StoppedInstances);
	Assert.NotNull(manager.GetSession(7));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter PinchtabInstanceManagerTests`
Expected: compile error — no `MarkHandoff`, no `HandedOff`, no `HandoffTimeout`, constructor arity mismatch.

- [ ] **Step 3: Implement handoff lifecycle**

In `Lis.Tools/Pinchtab/PinchtabInstanceManager.cs`:

Add `HandedOff` to the session class:

```csharp
public sealed class AgentBrowserSession {
	public required string InstanceId { get; init; }
	public required bool   Headed     { get; init; }
	public string?         TabId      { get; set; }
	public bool            HandedOff  { get; set; }
}
```

Change the primary constructor (options between client and logger):

```csharp
public sealed partial class PinchtabInstanceManager(
	IPinchtabClient                  client,
	PinchtabOptions                  options,
	ILogger<PinchtabInstanceManager> logger) : IDisposable {
```

Add fields/properties next to the existing fields:

```csharp
	private readonly Dictionary<long, CancellationTokenSource> handoffTimers = new();

	/// <summary>How long a handed-off instance survives without a new turn. Settable for tests.</summary>
	public TimeSpan HandoffTimeout { get; set; } = TimeSpan.FromMinutes(options.HandoffTimeoutMinutes);
```

Replace `Dispose`:

```csharp
	public void Dispose() {
		lock (this.turnLock) {
			foreach (CancellationTokenSource cts in this.handoffTimers.Values) {
				cts.Cancel();
				cts.Dispose();
			}
			this.handoffTimers.Clear();
		}

		this.gate.Dispose();
	}
```

Replace `BeginTurn` (cancel pending timer, reclaim session):

```csharp
	/// <summary>Marks the start of a conversation turn for an agent. Pair with <see cref="EndTurnAsync"/>.</summary>
	public void BeginTurn(long agentId) {
		lock (this.turnLock) {
			this.activeTurns[agentId] = this.activeTurns.GetValueOrDefault(agentId) + 1;

			if (this.handoffTimers.Remove(agentId, out CancellationTokenSource? cts)) {
				cts.Cancel();
				cts.Dispose();
			}
		}

		if (this.sessions.TryGetValue(agentId, out AgentBrowserSession? session) && session.HandedOff) {
			session.HandedOff = false;
			LogHandoffReclaimed(logger, agentId, session.InstanceId);
		}
	}
```

Add `MarkHandoff` after `GetSession`:

```csharp
	/// <summary>
	/// Flags the agent's headed session to survive turn end so the user can drive the browser.
	/// Returns the tool-facing result message (success or instructive error).
	/// </summary>
	public string MarkHandoff(long agentId) {
		if (!this.sessions.TryGetValue(agentId, out AgentBrowserSession? session))
			return "No browser session. Use browser-navigate with headed=true first.";
		if (!session.Headed) return "Current browser session is headless. Re-open the page with headed=true first.";

		session.HandedOff = true;
		LogHandoffMarked(logger, agentId, session.InstanceId);
		return "Browser handed off — finish your reply telling the user what to do.";
	}
```

In `CloseForAgentAsync`, insert the handoff branch between the turn-active check and `TryRemove`:

```csharp
			// A new turn may have started while we waited for the gate; keep the instance alive
			lock (this.turnLock) { if (this.activeTurns.GetValueOrDefault(agentId) > 0) return; }

			// A handed-off session survives turn end; the user is driving the browser
			if (this.sessions.TryGetValue(agentId, out AgentBrowserSession? pending) && pending.HandedOff) {
				this.ArmHandoffTimer(agentId);
				LogHandoffPersisted(logger, agentId, pending.InstanceId);
				return;
			}

			if (!this.sessions.TryRemove(agentId, out AgentBrowserSession? session)) return;
```

Add the timer methods after `MarkHandoff`:

```csharp
	private void ArmHandoffTimer(long agentId) {
		CancellationTokenSource cts = new();
		lock (this.turnLock) {
			if (this.handoffTimers.Remove(agentId, out CancellationTokenSource? old)) {
				old.Cancel();
				old.Dispose();
			}
			this.handoffTimers[agentId] = cts;
		}

		_ = this.ExpireHandoffAsync(agentId, cts.Token);
	}

	private async Task ExpireHandoffAsync(long agentId, CancellationToken ct) {
		try {
			await Task.Delay(this.HandoffTimeout, ct);
		} catch (OperationCanceledException) {
			return;
		}

		lock (this.turnLock) {
			// A turn started concurrently with the expiry; BeginTurn owns the timer now
			if (this.activeTurns.GetValueOrDefault(agentId) > 0) return;
			if (this.handoffTimers.Remove(agentId, out CancellationTokenSource? cts)) cts.Dispose();
		}

		if (this.sessions.TryGetValue(agentId, out AgentBrowserSession? session)) session.HandedOff = false;
		LogHandoffExpired(logger, agentId);
		await this.CloseForAgentAsync(agentId);
	}
```

Add the logger messages next to the existing ones:

```csharp
	[LoggerMessage(Level = LogLevel.Information, Message = "Browser handoff marked for agent {AgentId} (instance {InstanceId})")]
	private static partial void LogHandoffMarked(ILogger logger, long agentId, string instanceId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Handed-off instance {InstanceId} for agent {AgentId} persists past turn end")]
	private static partial void LogHandoffPersisted(ILogger logger, long agentId, string instanceId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} reclaimed handed-off instance {InstanceId}")]
	private static partial void LogHandoffReclaimed(ILogger logger, long agentId, string instanceId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Browser handoff for agent {AgentId} timed out; closing instance")]
	private static partial void LogHandoffExpired(ILogger logger, long agentId);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: PASS — all tests including the six new ones. (Run the whole suite: the constructor change and `BeginTurn` behavior affect other tests.)

- [ ] **Step 5: Commit**

```bash
git add Lis.Tools/Pinchtab/PinchtabInstanceManager.cs Lis.Tests/PinchtabInstanceManagerTests.cs
git commit -m "✨ feat(browser): persist handed-off headed instances past turn end"
```

---

### Task 3: `browser-handoff` tool + docs

**Files:**
- Modify: `Lis.Tools/BrowserPlugin.cs` (add function after `TabsAsync`, before the private helper)
- Modify: `docs/TOOLS.md:106-120` (browser function table + lifecycle note)
- Modify: `docs/BROWSER.md` (How it works bullet + Configuration table)

**Interfaces:**
- Consumes: `PinchtabInstanceManager.MarkHandoff(long agentId): string` (Task 2); `ToolContext.AgentId`, `ToolContext.NotifyAsync` (existing).

- [ ] **Step 1: Add the kernel function**

In `Lis.Tools/BrowserPlugin.cs`, after `TabsAsync` and before `GetTabOrThrowAsync`:

```csharp
	[Trace("BrowserPlugin > HandoffAsync")]
	[KernelFunction("handoff")]
	[Description("Hand the browser to the user for manual interaction (login, captcha, 2FA). Requires a headed session (browser-navigate with headed=true). The window stays open after your reply; the user's next message returns control to you.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> HandoffAsync(CancellationToken ct = default) {
		await ToolContext.NotifyAsync("🤝 Handing browser to you", ct);

		try {
			long agentId = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");
			return instances.MarkHandoff(agentId);
		} catch (Exception ex) {
			return $"Handoff error: {ex.Message}";
		}
	}
```

- [ ] **Step 2: Build and run the full suite**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: build 0 errors, all tests PASS. (The plugin is a thin passthrough over `MarkHandoff`, which Task 2 covered; no new unit test.)

- [ ] **Step 3: Update docs**

`docs/TOOLS.md` — add a row to the BrowserPlugin table after `tabs`:

```markdown
| `tabs` | List open tabs | OwnerOnly |
| `handoff` | Hand the headed browser to the user (login/captcha/2FA); survives turn end until the next message or timeout | OwnerOnly |
```

And extend the closing paragraph:

```markdown
Powered by the PinchTab sidecar (`PINCHTAB_ENABLED=true`). Each agent has its own browser profile (`agent-{agentId}`); instances start lazily and close at conversation turn end — unless handed off via `handoff`, in which case the instance survives until the user's next message or `PINCHTAB_HANDOFF_TIMEOUT_MINUTES`. See [BROWSER](BROWSER.md) for setup.
```

`docs/BROWSER.md` — add a bullet under "How it works" after the headless bullet:

```markdown
- `browser-handoff` keeps a headed instance alive past turn end so the user can log in / solve a captcha with the agent's cookies. The agent reclaims it on the user's next message; an idle timeout (`PINCHTAB_HANDOFF_TIMEOUT_MINUTES`) closes it otherwise.
```

And add a row to the Configuration table:

```markdown
| `PINCHTAB_HANDOFF_TIMEOUT_MINUTES` | `15` | Idle minutes before a handed-off headed instance is closed |
```

- [ ] **Step 4: Code cleanup and diff check**

```bash
DOTNET_ROOT=$HOME/.dotnet ~/.dotnet/tools/jb cleanupcode Lis.Tools/Lis.Tools.csproj --profile="Built-in: Full Cleanup" --settings=Lis.sln.DotSettings
git status --short   # verify only intended files changed
dotnet test Lis.Tests/Lis.Tests.csproj
```

Expected: only the files listed in this plan modified; tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Lis.Tools/BrowserPlugin.cs docs/TOOLS.md docs/BROWSER.md
git commit -m "✨ feat(browser): browser-handoff tool for manual user takeover"
```
