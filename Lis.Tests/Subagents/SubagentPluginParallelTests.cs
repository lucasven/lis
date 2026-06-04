using System.Collections.Concurrent;

using Lis.Core.Channel;
using Lis.Core.Subagents;
using Lis.Core.Util;
using Lis.Tools;

namespace Lis.Tests.Subagents;

public sealed class SubagentPluginParallelTests : IDisposable {

	public SubagentPluginParallelTests() {
		ToolContext.Depth                = 0;
		ToolContext.Channel              = null;
		ToolContext.ChatId               = null;
		ToolContext.NotificationsEnabled = false;
		ToolContext.AgentId              = null;
	}

	public void Dispose() {
		ToolContext.Depth                = 0;
		ToolContext.Channel              = null;
		ToolContext.ChatId               = null;
		ToolContext.NotificationsEnabled = false;
		ToolContext.AgentId              = null;
	}

	// -- parallel: all complete successfully ------------------------------------

	[Fact]
	public async Task SpawnParallel_AllSucceed_ReturnsCombinedResults() {
		var fakeRunner = new FakeSubagentRunner(result: "done");
		var plugin     = new SubagentPlugin(fakeRunner);

		ToolContext.Depth                = 0;
		ToolContext.AgentId              = 1;
		ToolContext.NotificationsEnabled = false;

		string result = await plugin.SpawnParallelAsync(["task A", "task B", "task C"]);

		Assert.Contains("Subagent 1/3 [Completed]", result);
		Assert.Contains("Subagent 2/3 [Completed]", result);
		Assert.Contains("Subagent 3/3 [Completed]", result);
		Assert.Equal(3, fakeRunner.CallCount);
	}

	// -- parallel: notifications stream as each finishes -----------------------

	[Fact]
	public async Task SpawnParallel_SendsNotificationPerSubagent() {
		var channel    = new ThreadSafeFakeChannel();
		var fakeRunner = new FakeSubagentRunner(result: "done");
		var plugin     = new SubagentPlugin(fakeRunner);

		ToolContext.Depth                = 0;
		ToolContext.AgentId              = 1;
		ToolContext.Channel              = channel;
		ToolContext.ChatId               = "test-chat";
		ToolContext.NotificationsEnabled = true;

		await plugin.SpawnParallelAsync(["task A", "task B"]);

		// 1 initial "Spawning..." notification + 2 result notifications
		Assert.Equal(3, channel.MessagesSent);
		Assert.Contains(channel.Messages, m => m.Contains("Subagent 1/2"));
		Assert.Contains(channel.Messages, m => m.Contains("Subagent 2/2"));
	}

	// -- parallel: one failure doesn't crash others ----------------------------

	[Fact]
	public async Task SpawnParallel_PartialFailure_AllResultsReturned() {
		var fakeRunner = new FakeSubagentRunner(failOnTaskContaining: "fail this");
		var plugin     = new SubagentPlugin(fakeRunner);

		ToolContext.Depth                = 0;
		ToolContext.AgentId              = 1;
		ToolContext.NotificationsEnabled = false;

		string result = await plugin.SpawnParallelAsync(["good task", "fail this one", "another good task"]);

		Assert.Contains("[Completed]", result);
		Assert.Contains("[Failed]", result);
		Assert.Contains("[error]", result);
		Assert.Equal(3, fakeRunner.CallCount);
	}

	// -- parallel: failed subagent sends ❌ notification -------------------------

	[Fact]
	public async Task SpawnParallel_FailedSubagent_SendsErrorNotification() {
		var channel    = new ThreadSafeFakeChannel();
		var fakeRunner = new FakeSubagentRunner(failOnTaskContaining: "fail");
		var plugin     = new SubagentPlugin(fakeRunner);

		ToolContext.Depth                = 0;
		ToolContext.AgentId              = 1;
		ToolContext.Channel              = channel;
		ToolContext.ChatId               = "test-chat";
		ToolContext.NotificationsEnabled = true;

		await plugin.SpawnParallelAsync(["fail this"]);

		Assert.Contains(channel.Messages, m => m.Contains('❌'));
	}

	// -- parallel: empty tasks list -------------------------------------------

	[Fact]
	public async Task SpawnParallel_EmptyTasks_ReturnsError() {
		var plugin = new SubagentPlugin(new FakeSubagentRunner(result: "x"));

		ToolContext.Depth   = 0;
		ToolContext.AgentId = 1;

		string result = await plugin.SpawnParallelAsync([]);

		Assert.Contains("At least one task is required", result);
	}

	// -- parallel: depth exceeded ---------------------------------------------

	[Fact]
	public async Task SpawnParallel_DepthExceeded_ReturnsError() {
		var plugin = new SubagentPlugin(new FakeSubagentRunner(result: "x"));

		ToolContext.Depth   = 3;
		ToolContext.AgentId = 1;

		string result = await plugin.SpawnParallelAsync(["task"]);

		Assert.Contains("Maximum subagent nesting depth (3) exceeded", result);
	}

	// -- parallel: no agent context -------------------------------------------

	[Fact]
	public async Task SpawnParallel_NoAgentId_ReturnsError() {
		var plugin = new SubagentPlugin(new FakeSubagentRunner(result: "x"));

		ToolContext.Depth   = 0;
		ToolContext.AgentId = null;

		string result = await plugin.SpawnParallelAsync(["task"]);

		Assert.Contains("No agent context available", result);
	}

	// -- parallel: tasks actually run concurrently ----------------------------

	[Fact]
	public async Task SpawnParallel_TasksRunConcurrently() {
		var fakeRunner = new DelayedSubagentRunner(delay: TimeSpan.FromMilliseconds(200));
		var plugin     = new SubagentPlugin(fakeRunner);

		ToolContext.Depth                = 0;
		ToolContext.AgentId              = 1;
		ToolContext.NotificationsEnabled = false;

		var sw = System.Diagnostics.Stopwatch.StartNew();
		await plugin.SpawnParallelAsync(["task A", "task B", "task C"]);
		sw.Stop();

		// 3 tasks × 200ms each = 600ms serial. Parallel should be ~200ms.
		Assert.True(sw.ElapsedMilliseconds < 500, $"Expected <500ms but took {sw.ElapsedMilliseconds}ms — tasks may not be parallel");
	}

	// -- test infrastructure --------------------------------------------------

	private sealed class FakeSubagentRunner(string result = "done", string? failOnTaskContaining = null) : ISubagentRunner {
		private int callCount;
		public int CallCount => this.callCount;

		public Task<SubagentResult> RunAsync(SubagentRequest request, long agentId, CancellationToken ct = default) {
			Interlocked.Increment(ref this.callCount);

			if (failOnTaskContaining is not null && request.Task.Contains(failOnTaskContaining))
				return Task.FromResult(new SubagentResult {
					Status = SubagentStatus.Failed,
					Error  = "Simulated failure",
				});

			return Task.FromResult(new SubagentResult {
				Status = SubagentStatus.Completed,
				Result = result,
				Usage  = new SubagentTokenUsage { InputTokens = 100, OutputTokens = 50 },
			});
		}
	}

	private sealed class DelayedSubagentRunner(TimeSpan delay) : ISubagentRunner {
		public async Task<SubagentResult> RunAsync(SubagentRequest request, long agentId, CancellationToken ct = default) {
			await Task.Delay(delay, ct);
			return new SubagentResult {
				Status = SubagentStatus.Completed,
				Result = "delayed result",
				Usage  = new SubagentTokenUsage { InputTokens = 100, OutputTokens = 50 },
			};
		}
	}

	private sealed class ThreadSafeFakeChannel : IChannelClient {
		private int messagesSent;
		private readonly ConcurrentBag<string> messages = [];

		public int             MessagesSent => this.messagesSent;
		public IReadOnlyCollection<string> Messages => this.messages;

		public Task<string?> SendMessageAsync(string chatId, string message, string? replyToId = null, CancellationToken ct = default) {
			Interlocked.Increment(ref this.messagesSent);
			this.messages.Add(message);
			return Task.FromResult<string?>(null);
		}

		public Task SetTypingAsync(string chatId, CancellationToken ct = default) =>
			Task.CompletedTask;

		public Task StopTypingAsync(string chatId, CancellationToken ct = default) =>
			Task.CompletedTask;

		public Task MarkReadAsync(string messageId, string chatId, CancellationToken ct = default) =>
			Task.CompletedTask;

		public Task ReactAsync(string messageId, string chatId, string emoji, CancellationToken ct = default) =>
			Task.CompletedTask;

		public Task<MediaDownload?> DownloadMediaAsync(string messageId, string chatId, string? mediaPath = null, CancellationToken ct = default) =>
			Task.FromResult<MediaDownload?>(null);

		public Task<string?> SendFileAsync(string chatId, MediaUpload media,
			string? caption = null, string? replyToId = null, CancellationToken ct = default) =>
			throw new NotSupportedException("File sending is not supported in tests.");
	}
}
