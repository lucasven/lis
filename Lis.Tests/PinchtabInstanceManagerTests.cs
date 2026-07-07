using Lis.Tools.Pinchtab;

using Microsoft.Extensions.Logging.Abstractions;

namespace Lis.Tests;

public class PinchtabInstanceManagerTests {

	private sealed class FakePinchtabClient : IPinchtabClient {
		public List<PinchtabProfile>  Profiles         = new();
		public List<string>           StoppedProfiles  = new();
		public List<string>           StoppedInstances = new();
		public List<(string ProfileId, bool Headed)> Started = new();
		public HashSet<string>        DeadInstances    = new();
		private int nextId = 1;

		public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(true);

		public Task<PinchtabProfile?> FindProfileAsync(string name, CancellationToken ct = default) =>
			Task.FromResult(this.Profiles.FirstOrDefault(p => p.Name == name));

		public Task<PinchtabProfile> CreateProfileAsync(string name, CancellationToken ct = default) {
			PinchtabProfile profile = new($"prof_{this.nextId++}", name);
			this.Profiles.Add(profile);
			return Task.FromResult(profile);
		}

		public Task StopProfileAsync(string profileId, CancellationToken ct = default) {
			this.StoppedProfiles.Add(profileId);
			return Task.CompletedTask;
		}

		public Task<PinchtabInstance> StartInstanceAsync(string profileId, bool headed, CancellationToken ct = default) {
			this.Started.Add((profileId, headed));
			return Task.FromResult(new PinchtabInstance($"inst_{this.nextId++}", profileId, "9999", "running"));
		}

		public Task<PinchtabInstance?> GetInstanceAsync(string instanceId, CancellationToken ct = default) =>
			Task.FromResult(this.DeadInstances.Contains(instanceId)
				? (PinchtabInstance?)null
				: new PinchtabInstance(instanceId, null, "9999", "running"));

		public Task<string> GetInstanceLogsAsync(string instanceId, CancellationToken ct = default) => Task.FromResult("");

		public Task StopInstanceAsync(string instanceId, CancellationToken ct = default) {
			this.StoppedInstances.Add(instanceId);
			return Task.CompletedTask;
		}

		public Task<PinchtabTab> OpenTabAsync(string instanceId, string url, CancellationToken ct = default) =>
			Task.FromResult(new PinchtabTab("tab_1", url, "Title"));

		public Task<string> ListTabsAsync(string instanceId, CancellationToken ct = default) => Task.FromResult("[]");
		public Task<string> NavigateAsync(string tabId, string url, CancellationToken ct = default) => Task.FromResult("{}");
		public Task<string> SnapshotAsync(string tabId, string filter, int maxTokens, CancellationToken ct = default) => Task.FromResult("");
		public Task<string> ActionAsync(string tabId, object payload, CancellationToken ct = default) => Task.FromResult("{}");
		public Task<string> EvaluateAsync(string tabId, string expression, CancellationToken ct = default) => Task.FromResult("{}");
		public Task<byte[]> ScreenshotAsync(string tabId, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
		public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
	}

	private static PinchtabInstanceManager CreateManager(FakePinchtabClient client) =>
		new(client, NullLogger<PinchtabInstanceManager>.Instance);

	[Fact]
	public async Task Creates_profile_and_starts_instance_on_first_call() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession session = await manager.EnsureSessionAsync(agentId: 3, headed: false, CancellationToken.None);

		Assert.Single(client.Profiles);
		Assert.Equal("agent-3", client.Profiles[0].Name);
		Assert.Single(client.Started);
		Assert.False(client.Started[0].Headed);
		Assert.NotNull(session.InstanceId);
		Assert.False(session.Headed);
		Assert.Contains(client.Profiles[0].Id, client.StoppedProfiles);
	}

	[Fact]
	public async Task Reuses_running_instance_on_second_call() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession first  = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);
		AgentBrowserSession second = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);

		Assert.Same(first, second);
		Assert.Single(client.Started);
	}

	[Fact]
	public async Task Reuses_existing_profile() {
		FakePinchtabClient client = new();
		client.Profiles.Add(new PinchtabProfile("prof_existing", "agent-3"));
		PinchtabInstanceManager manager = CreateManager(client);

		await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);

		Assert.Single(client.Profiles);
		Assert.Equal("prof_existing", client.Started[0].ProfileId);
	}

	[Fact]
	public async Task Restarts_when_tracked_instance_is_dead() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession first = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);
		client.DeadInstances.Add(first.InstanceId);

		AgentBrowserSession second = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);

		Assert.NotSame(first, second);
		Assert.Equal(2, client.Started.Count);
		Assert.Equal(2, client.StoppedProfiles.Count);
	}

	[Fact]
	public async Task Restarts_instance_when_mode_changes() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession headless = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);
		AgentBrowserSession headed   = await manager.EnsureSessionAsync(3, headed: true, CancellationToken.None);

		Assert.NotSame(headless, headed);
		Assert.Contains(headless.InstanceId, client.StoppedInstances);
		Assert.True(headed.Headed);
		Assert.Equal(2, client.Started.Count);
	}

	[Fact]
	public async Task Close_stops_instance_and_forgets_session() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession session = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);
		await manager.CloseForAgentAsync(3);

		Assert.Contains(session.InstanceId, client.StoppedInstances);
		Assert.Null(manager.GetSession(3));
	}

	[Fact]
	public async Task Close_is_noop_without_session() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		await manager.CloseForAgentAsync(42);

		Assert.Empty(client.StoppedInstances);
	}

	[Fact]
	public async Task EndTurn_keeps_instance_alive_while_another_turn_is_running() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		manager.BeginTurn(3);
		manager.BeginTurn(3);
		AgentBrowserSession session = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);

		await manager.EndTurnAsync(3);

		Assert.Empty(client.StoppedInstances);
		Assert.Same(session, manager.GetSession(3));
	}

	[Fact]
	public async Task EndTurn_closes_instance_when_last_turn_finishes() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		manager.BeginTurn(3);
		manager.BeginTurn(3);
		AgentBrowserSession session = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);

		await manager.EndTurnAsync(3);
		await manager.EndTurnAsync(3);

		Assert.Contains(session.InstanceId, client.StoppedInstances);
		Assert.Null(manager.GetSession(3));
	}

	[Fact]
	public async Task Close_is_skipped_when_a_new_turn_started_meanwhile() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession session = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);
		manager.BeginTurn(3);

		await manager.CloseForAgentAsync(3);

		Assert.Empty(client.StoppedInstances);
		Assert.Same(session, manager.GetSession(3));
	}

	[Fact]
	public async Task EndTurn_without_session_is_noop() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		manager.BeginTurn(42);
		await manager.EndTurnAsync(42);

		Assert.Empty(client.StoppedInstances);
	}
}
