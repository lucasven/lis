using System.Collections.Concurrent;

using Lis.Core.Util;

using Microsoft.Extensions.Logging;

namespace Lis.Tools.Pinchtab;

/// <summary>A live browser instance owned by one agent.</summary>
public sealed class AgentBrowserSession {
	public required string InstanceId { get; init; }
	public required bool   Headed     { get; init; }
	public string?         TabId      { get; set; }
	public bool            HandedOff  { get; set; }
}

/// <summary>
/// Tracks one pinchtab browser instance per agent (profile "agent-{agentId}").
/// Instances start lazily on first browser tool call and close at conversation turn end.
/// </summary>
public sealed partial class PinchtabInstanceManager(
	IPinchtabClient                  client,
	PinchtabOptions                  options,
	ILogger<PinchtabInstanceManager> logger) : IDisposable {

	private readonly ConcurrentDictionary<long, AgentBrowserSession> sessions     = new();
	private readonly Dictionary<long, int>                           activeTurns  = new();
	private readonly Dictionary<long, CancellationTokenSource>       handoffTimers = new();
	private readonly SemaphoreSlim gate     = new(1, 1);
	private readonly Lock          turnLock = new();

	/// <summary>How long a handed-off instance survives without a new turn. Settable for tests.</summary>
	public TimeSpan HandoffTimeout { get; set; } = TimeSpan.FromMinutes(options.HandoffTimeoutMinutes);

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

	/// <summary>
	/// Marks the end of a conversation turn. Closes the agent's browser instance only when
	/// no other turn for the same agent is still running (turns are reference-counted).
	/// </summary>
	[Trace("PinchtabInstanceManager > EndTurnAsync")]
	public async Task EndTurnAsync(long agentId) {
		lock (this.turnLock) {
			int remaining = this.activeTurns.GetValueOrDefault(agentId) - 1;
			if (remaining > 0) { this.activeTurns[agentId] = remaining; return; }
			this.activeTurns.Remove(agentId);
		}

		await this.CloseForAgentAsync(agentId);
	}

	[Trace("PinchtabInstanceManager > EnsureSessionAsync")]
	public async Task<AgentBrowserSession> EnsureSessionAsync(long agentId, bool headed, CancellationToken ct) {
		await this.gate.WaitAsync(ct);
		try {
			// Reuse a live instance with matching mode; drop dead or mode-mismatched ones
			if (this.sessions.TryGetValue(agentId, out AgentBrowserSession? existing)) {
				PinchtabInstance? live = await client.GetInstanceAsync(existing.InstanceId, ct);

				if (live is { Status: "running" } && existing.Headed == headed) return existing;

				if (live is { Status: "running" }) {
					LogModeChange(logger, agentId, headed);
					await client.StopInstanceAsync(existing.InstanceId, ct);
				}
				this.sessions.TryRemove(agentId, out _);
			}

			string profileId = await this.EnsureProfileAsync(agentId, ct);

			if (headed) {
				string? display    = Environment.GetEnvironmentVariable("DISPLAY");
				string? wayland    = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
				string? xauthority = Environment.GetEnvironmentVariable("XAUTHORITY");
				LogHeadedDisplay(logger, display, wayland, xauthority);
			}

			// A profile can only have one active instance; stop any stale one (ignore errors)
			try { await client.StopProfileAsync(profileId, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }

			PinchtabInstance instance = await client.StartInstanceAsync(profileId, headed, ct);
			try {
				instance = await this.WaitRunningAsync(instance, ct);
			} catch (InvalidOperationException) {
				await this.RelayInstanceLogsAsync(instance.Id, ct);
				throw;
			}

			// Log what pinchtab acknowledged (not what we requested) to catch mode/browser fallbacks
			LogInstanceEcho(logger, instance.Id, instance.Mode, instance.Browser, instance.FallbackFrom, instance.FallbackReason);
			string requestedMode = headed ? "headed" : "headless";
			if (instance.Mode is not null && instance.Mode != requestedMode)
				LogModeMismatch(logger, instance.Id, requestedMode, instance.Mode);
			if (headed) await this.RelayInstanceLogsAsync(instance.Id, ct);

			AgentBrowserSession session = new() { InstanceId = instance.Id, Headed = headed };
			this.sessions[agentId] = session;
			LogStarted(logger, agentId, instance.Id, headed);
			return session;
		} finally {
			this.gate.Release();
		}
	}

	public AgentBrowserSession? GetSession(long agentId) =>
		this.sessions.TryGetValue(agentId, out AgentBrowserSession? session) ? session : null;

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

	[Trace("PinchtabInstanceManager > CloseForAgentAsync")]
	public async Task CloseForAgentAsync(long agentId) {
		await this.gate.WaitAsync();
		try {
			// A new turn may have started while we waited for the gate; keep the instance alive
			lock (this.turnLock) { if (this.activeTurns.GetValueOrDefault(agentId) > 0) return; }

			// A handed-off session survives turn end; the user is driving the browser
			if (this.sessions.TryGetValue(agentId, out AgentBrowserSession? pending) && pending.HandedOff) {
				this.ArmHandoffTimer(agentId);
				LogHandoffPersisted(logger, agentId, pending.InstanceId);
				return;
			}

			if (!this.sessions.TryRemove(agentId, out AgentBrowserSession? session)) return;

			try {
				await client.StopInstanceAsync(session.InstanceId);
				LogClosed(logger, agentId, session.InstanceId);
			} catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
				LogCloseFailed(logger, ex, agentId, session.InstanceId);
			}
		} finally {
			this.gate.Release();
		}
	}

	private async Task<string> EnsureProfileAsync(long agentId, CancellationToken ct) {
		string name = $"agent-{agentId}";

		PinchtabProfile? profile = await client.FindProfileAsync(name, ct);
		profile ??= await client.CreateProfileAsync(name, ct);

		return profile.Id;
	}

	/// <summary>Fetches the per-instance bridge ring buffer (chrome launch line, chrome stderr) into our logs.</summary>
	private async Task RelayInstanceLogsAsync(string instanceId, CancellationToken ct) {
		try {
			string logs = await client.GetInstanceLogsAsync(instanceId, ct);
			if (logs.Length > 8000) logs = logs[^8000..];
			LogInstanceLogs(logger, instanceId, logs);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			LogInstanceLogsFailed(logger, ex, instanceId);
		}
	}

	private async Task<PinchtabInstance> WaitRunningAsync(PinchtabInstance instance, CancellationToken ct) {
		if (instance.Status == "running") return instance;

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));

		while (true) {
			await Task.Delay(250, timeout.Token);
			PinchtabInstance? current = await client.GetInstanceAsync(instance.Id, timeout.Token);

			if (current is { Status: "running" }) return current;
			if (current is null or { Status: "stopped" })
				throw new InvalidOperationException($"Pinchtab instance {instance.Id} failed to start.");
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Starting headed instance with DISPLAY={Display}, WAYLAND_DISPLAY={WaylandDisplay}, XAUTHORITY={Xauthority}")]
	private static partial void LogHeadedDisplay(ILogger logger, string? display, string? waylandDisplay, string? xauthority);

	[LoggerMessage(Level = LogLevel.Information, Message = "Pinchtab instance {InstanceId} echo: mode={Mode}, browser={Browser}, fallbackFrom={FallbackFrom}, fallbackReason={FallbackReason}")]
	private static partial void LogInstanceEcho(ILogger logger, string instanceId, string? mode, string? browser, string? fallbackFrom, string? fallbackReason);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Pinchtab instance {InstanceId} mode mismatch: requested {Requested} but pinchtab acknowledged {Acknowledged}")]
	private static partial void LogModeMismatch(ILogger logger, string instanceId, string requested, string acknowledged);

	[LoggerMessage(Level = LogLevel.Information, Message = "Pinchtab instance {InstanceId} bridge logs:\n{Logs}")]
	private static partial void LogInstanceLogs(ILogger logger, string instanceId, string logs);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fetch bridge logs for pinchtab instance {InstanceId}")]
	private static partial void LogInstanceLogsFailed(ILogger logger, Exception ex, string instanceId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Started pinchtab instance {InstanceId} for agent {AgentId} (headed: {Headed})")]
	private static partial void LogStarted(ILogger logger, long agentId, string instanceId, bool headed);

	[LoggerMessage(Level = LogLevel.Information, Message = "Restarting pinchtab instance for agent {AgentId} due to mode change (headed: {Headed})")]
	private static partial void LogModeChange(ILogger logger, long agentId, bool headed);

	[LoggerMessage(Level = LogLevel.Information, Message = "Closed pinchtab instance {InstanceId} for agent {AgentId}")]
	private static partial void LogClosed(ILogger logger, long agentId, string instanceId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to close pinchtab instance {InstanceId} for agent {AgentId}")]
	private static partial void LogCloseFailed(ILogger logger, Exception ex, long agentId, string instanceId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Browser handoff marked for agent {AgentId} (instance {InstanceId})")]
	private static partial void LogHandoffMarked(ILogger logger, long agentId, string instanceId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Handed-off instance {InstanceId} for agent {AgentId} persists past turn end")]
	private static partial void LogHandoffPersisted(ILogger logger, long agentId, string instanceId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} reclaimed handed-off instance {InstanceId}")]
	private static partial void LogHandoffReclaimed(ILogger logger, long agentId, string instanceId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Browser handoff for agent {AgentId} timed out; closing instance")]
	private static partial void LogHandoffExpired(ILogger logger, long agentId);
}
