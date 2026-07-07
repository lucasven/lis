using System.Collections.Concurrent;

using Lis.Core.Util;

using Microsoft.Extensions.Logging;

namespace Lis.Tools.Pinchtab;

/// <summary>A live browser instance owned by one agent.</summary>
public sealed class AgentBrowserSession {
	public required string InstanceId { get; init; }
	public required bool   Headed     { get; init; }
	public string?         TabId      { get; set; }
}

/// <summary>
/// Tracks one pinchtab browser instance per agent (profile "agent-{agentId}").
/// Instances start lazily on first browser tool call and close at conversation turn end.
/// </summary>
public sealed partial class PinchtabInstanceManager(
	IPinchtabClient                  client,
	ILogger<PinchtabInstanceManager> logger) : IDisposable {

	private readonly ConcurrentDictionary<long, AgentBrowserSession> sessions = new();
	private readonly Dictionary<long, int> activeTurns = new();
	private readonly SemaphoreSlim gate = new(1, 1);
	private readonly Lock turnLock = new();

	public void Dispose() => this.gate.Dispose();

	/// <summary>Marks the start of a conversation turn for an agent. Pair with <see cref="EndTurnAsync"/>.</summary>
	public void BeginTurn(long agentId) {
		lock (this.turnLock) this.activeTurns[agentId] = this.activeTurns.GetValueOrDefault(agentId) + 1;
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
			instance = await this.WaitRunningAsync(instance, ct);

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

	[Trace("PinchtabInstanceManager > CloseForAgentAsync")]
	public async Task CloseForAgentAsync(long agentId) {
		await this.gate.WaitAsync();
		try {
			// A new turn may have started while we waited for the gate; keep the instance alive
			lock (this.turnLock) { if (this.activeTurns.GetValueOrDefault(agentId) > 0) return; }

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

	[LoggerMessage(Level = LogLevel.Information, Message = "Started pinchtab instance {InstanceId} for agent {AgentId} (headed: {Headed})")]
	private static partial void LogStarted(ILogger logger, long agentId, string instanceId, bool headed);

	[LoggerMessage(Level = LogLevel.Information, Message = "Restarting pinchtab instance for agent {AgentId} due to mode change (headed: {Headed})")]
	private static partial void LogModeChange(ILogger logger, long agentId, bool headed);

	[LoggerMessage(Level = LogLevel.Information, Message = "Closed pinchtab instance {InstanceId} for agent {AgentId}")]
	private static partial void LogClosed(ILogger logger, long agentId, string instanceId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to close pinchtab instance {InstanceId} for agent {AgentId}")]
	private static partial void LogCloseFailed(ILogger logger, Exception ex, long agentId, string instanceId);
}
