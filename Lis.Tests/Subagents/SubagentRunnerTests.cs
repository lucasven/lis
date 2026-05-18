using System.Runtime.CompilerServices;

using Lis.Agent;
using Lis.Core.Channel;
using Lis.Core.Configuration;
using Lis.Core.Subagents;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;
using Lis.Tools;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Lis.Tests.Subagents;

public sealed class SubagentRunnerTests : IDisposable {

	public SubagentRunnerTests() {
		ToolContext.Depth                = 0;
		ToolContext.Channel              = null;
		ToolContext.NotificationsEnabled = false;
		ToolContext.MessageExternalId    = null;
		ToolContext.CacheBreakIndex      = 0;
		ToolContext.AgentId              = null;
	}

	public void Dispose() {
		ToolContext.Depth                = 0;
		ToolContext.Channel              = null;
		ToolContext.NotificationsEnabled = false;
		ToolContext.MessageExternalId    = null;
		ToolContext.CacheBreakIndex      = 0;
		ToolContext.AgentId              = null;
	}

	// -- R1, R2, R4: basic spawn with isolated context and result passback -------

	[Fact]
	public async Task Spawn_BasicTask_ReturnsCompletedResult() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard");
		ToolContext.Depth = 0;

		var request = new SubagentRequest { Task = "What is 2 + 2? Reply with just the number." };
		var result  = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Completed, result.Status);
		Assert.NotNull(result.Result);
		Assert.False(string.IsNullOrWhiteSpace(result.Result));
		Assert.Null(result.Error);
	}

	// -- R4: plugin serialization ------------------------------------------------

	[Fact]
	public async Task Spawn_Success_PluginReturnsResultText() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard");
		ToolContext.Depth = 0;

		var request = new SubagentRequest { Task = "Say hello." };
		var result  = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Completed, result.Status);
		Assert.NotNull(result.Result);
	}

	// -- R5: model override (same provider) --------------------------------------

	[Fact]
	public async Task Spawn_WithModelOverride_SameProvider_UsesSpecifiedModel() {
		var spy    = new ProviderSpy();
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard", providerSpy: spy);
		ToolContext.Depth = 0;

		var request = new SubagentRequest {
			Task  = "Summarize: The quick brown fox jumps over the lazy dog.",
			Model = "claude-haiku-4-5-20251001",
		};
		var result = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Completed, result.Status);
		Assert.Equal("anthropic", spy.ResolvedProvider);
		Assert.Equal("claude-haiku-4-5-20251001", spy.ResolvedModelId);
	}

	// -- R5: model override (cross-provider) -------------------------------------

	[Fact]
	public async Task Spawn_WithModelOverride_CrossProvider_ResolvesDifferentClient() {
		var spy    = new ProviderSpy();
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard", providerSpy: spy);
		ToolContext.Depth = 0;

		var request = new SubagentRequest {
			Task  = "Say hello.",
			Model = "openai:gpt-4o",
		};
		var result = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Completed, result.Status);
		Assert.Equal("openai", spy.ResolvedProvider);
		Assert.Equal("gpt-4o", spy.ResolvedModelId);
	}

	// -- R5: cross-provider with unregistered provider ---------------------------

	[Fact]
	public async Task Spawn_CrossProvider_UnknownProvider_ReturnsFailedResult() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard");
		ToolContext.Depth = 0;

		var request = new SubagentRequest {
			Task  = "This will fail.",
			Model = "nonexistent:some-model",
		};
		var result = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Failed, result.Status);
		Assert.Null(result.Result);
		Assert.NotNull(result.Error);
	}

	[Fact]
	public async Task Spawn_WithNullModel_InheritsParentModelAndProvider() {
		var spy    = new ProviderSpy();
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard", providerSpy: spy);
		ToolContext.Depth = 0;

		var request = new SubagentRequest { Task = "Say hello.", Model = null };
		var result  = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Completed, result.Status);
		Assert.Equal("anthropic", spy.ResolvedProvider);
		Assert.Equal("claude-sonnet-4-6-20250514", spy.ResolvedModelId);
	}

	// -- R3: tool profile inheritance --------------------------------------------

	[Fact]
	public async Task Spawn_InheritsToolProfile_CanUseDateTimeTool() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard");
		ToolContext.Depth = 0;

		var request = new SubagentRequest { Task = "What is today's date? Use the dt_get_current_datetime tool." };
		var result  = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Completed, result.Status);
		Assert.NotNull(result.Result);
	}

	// -- R6, R7: nesting depth ---------------------------------------------------

	[Fact]
	public async Task Spawn_AtDepth2_Succeeds() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard");
		ToolContext.Depth = 2;

		var request = new SubagentRequest { Task = "What is 5 + 5? Reply with just the number." };
		var result  = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Completed, result.Status);
	}

	[Fact]
	public async Task Spawn_AtDepth3_ReturnsDepthExceededError() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard");
		ToolContext.Depth = 3;

		var request = new SubagentRequest { Task = "This should not execute." };
		var result  = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Failed, result.Status);
		Assert.Null(result.Result);
		Assert.Contains("Maximum subagent nesting depth (3) exceeded", result.Error);
	}

	[Fact]
	public async Task Spawn_AtDepth0_DoesNotMutateParentDepth() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard");
		ToolContext.Depth = 0;

		var request = new SubagentRequest { Task = "Say hello." };
		await runner.RunAsync(request, agentId: 1);

		Assert.Equal(0, ToolContext.Depth);
	}

	// -- R8, R11: user isolation -------------------------------------------------

	[Fact]
	public async Task Spawn_SubagentContext_HasNullChannelAndDisabledNotifications() {
		var contextCapture = new ToolContextCapture();
		var fakeChannel    = new FakeChannel();
		var runner         = CreateRunner(
			agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514",
			toolProfile: "standard", contextCapture: contextCapture);

		ToolContext.Depth                = 0;
		ToolContext.NotificationsEnabled = true;
		ToolContext.Channel              = fakeChannel;
		ToolContext.MessageExternalId    = "parent-msg-123";
		ToolContext.CacheBreakIndex      = 5;

		var request = new SubagentRequest { Task = "Check context isolation." };
		var result  = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Completed, result.Status);

		Assert.Null(contextCapture.CapturedChannel);
		Assert.False(contextCapture.CapturedNotificationsEnabled);
		Assert.Null(contextCapture.CapturedMessageExternalId);
		Assert.Equal(-1, contextCapture.CapturedCacheBreakIndex);

		Assert.True(ToolContext.NotificationsEnabled);
		Assert.NotNull(ToolContext.Channel);
		Assert.Equal("parent-msg-123", ToolContext.MessageExternalId);
		Assert.Equal(5, ToolContext.CacheBreakIndex);

		Assert.Equal(0, fakeChannel.MessagesSent);
	}

	// -- R9: error containment ---------------------------------------------------

	[Fact]
	public async Task Spawn_LlmError_ReturnsFailedWithoutCrashingParent() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard", simulateLlmError: true);
		ToolContext.Depth = 0;

		var request = new SubagentRequest { Task = "This will fail." };
		var result  = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Failed, result.Status);
		Assert.NotNull(result.Error);
		Assert.Null(result.Result);
	}

	// -- R10: parallel execution -------------------------------------------------

	[Fact]
	public async Task Spawn_Parallel_BothCompleteWithIndependentContexts() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard");
		ToolContext.Depth = 0;

		var task1 = runner.RunAsync(new SubagentRequest { Task = "What is 10 + 10?" }, agentId: 1);
		var task2 = runner.RunAsync(new SubagentRequest { Task = "What is 20 + 20?" }, agentId: 1);

		var results = await Task.WhenAll(task1, task2);

		Assert.Equal(2, results.Length);
		Assert.All(results, r => Assert.Equal(SubagentStatus.Completed, r.Status));
		Assert.All(results, r => Assert.NotNull(r.Result));
	}

	// -- R1: empty task rejection ------------------------------------------------

	[Fact]
	public async Task Spawn_EmptyTask_ReturnsError() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard");
		ToolContext.Depth = 0;

		var request = new SubagentRequest { Task = "" };
		var result  = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Failed, result.Status);
		Assert.Contains("Task description is required", result.Error);
	}

	// -- R12: token tracking -----------------------------------------------------

	[Fact]
	public async Task Spawn_ReturnsTokenUsage() {
		var runner = CreateRunner(agentProvider: "anthropic", agentModel: "claude-sonnet-4-6-20250514", toolProfile: "standard");
		ToolContext.Depth = 0;

		var request = new SubagentRequest { Task = "What is 2 + 2? Reply with just the number." };
		var result  = await runner.RunAsync(request, agentId: 1);

		Assert.Equal(SubagentStatus.Completed, result.Status);
		Assert.NotNull(result.Usage);
		Assert.True(result.Usage.InputTokens > 0);
		Assert.True(result.Usage.OutputTokens > 0);
	}

	// -- Test infrastructure -----------------------------------------------------

	private static SubagentRunner CreateRunner(
		string              agentProvider,
		string              agentModel,
		string              toolProfile,
		bool                simulateLlmError = false,
		ProviderSpy?        providerSpy      = null,
		ToolContextCapture? contextCapture   = null) {

		DbContextOptions<LisDbContext> dbOptions = new DbContextOptionsBuilder<LisDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString())
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.Options;

		TestDbContext db = new(dbOptions);
		db.Agents.Add(new AgentEntity {
			Id          = 1,
			Name        = "test-agent",
			DisplayName = "Test Agent",
			Provider    = agentProvider,
			Model       = agentModel,
			MaxTokens   = 4096,
			ToolProfile = toolProfile,
			CreatedAt   = DateTimeOffset.UtcNow,
			UpdatedAt   = DateTimeOffset.UtcNow,
		});
		db.SaveChanges();

		FakeChatClient anthropicClient = new("anthropic", "Test response from subagent.", simulateLlmError, providerSpy, contextCapture);
		FakeChatClient openaiClient    = new("openai", "Test response from OpenAI.", false, providerSpy, contextCapture);

		ServiceCollection services = new();
		services.AddScoped<LisDbContext>(_ => new TestDbContext(dbOptions));
		services.AddKeyedSingleton<IChatClient>("anthropic", anthropicClient);
		services.AddKeyedSingleton<IChatClient>("openai", openaiClient);
		services.AddKeyedSingleton<IUsageExtractor>("anthropic", new FakeUsageExtractor());
		services.AddKeyedSingleton<IUsageExtractor>("openai", new FakeUsageExtractor());
		ServiceProvider sp = services.BuildServiceProvider();

		// Build kernel with DateTimePlugin (for tool inheritance test)
		IKernelBuilder kernelBuilder = Kernel.CreateBuilder();
		kernelBuilder.Services.AddSingleton<IChatCompletionService>(anthropicClient.AsChatCompletionService());
		Kernel kernel = kernelBuilder.Build();
		kernel.Plugins.AddFromType<DateTimePlugin>(pluginName: "dt");

		ToolAuthRegistry authRegistry = new();
		authRegistry.Build(kernel);

		ToolRunner toolRunner = new(authRegistry, new FakeApprovalService(), NullLogger<ToolRunner>.Instance);

		ToolPolicyService toolPolicyService = new();

		PromptComposer promptComposer = new(
			Options.Create(new LisOptions()),
			NullLogger<PromptComposer>.Instance);

		return new SubagentRunner(
			sp.GetRequiredService<IServiceScopeFactory>(),
			sp, kernel, toolRunner, toolPolicyService, promptComposer,
			NullLogger<SubagentRunner>.Instance);
	}

	private sealed class TestDbContext(DbContextOptions<LisDbContext> options) : LisDbContext(options) {
		protected override void OnModelCreating(ModelBuilder modelBuilder) {
			base.OnModelCreating(modelBuilder);
			modelBuilder.Entity<MemoryEntity>().Ignore(e => e.Embedding);
			modelBuilder.Entity<SessionEntity>().Ignore(e => e.SummaryEmbedding);
		}
	}

	private sealed class FakeChatClient(
		string providerName, string responseText, bool simulateError = false,
		ProviderSpy? spy = null, ToolContextCapture? contextCapture = null) : IChatClient {

		public ChatClientMetadata Metadata { get; } = new("fake");

		public Task<ChatResponse> GetResponseAsync(
			IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) {

			this.RecordSpy(options);
			contextCapture?.Capture();

			if (simulateError) throw new InvalidOperationException("Simulated LLM error");

			ChatMessage response = new(ChatRole.Assistant, responseText);
			return Task.FromResult(new ChatResponse(response));
		}

		public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
			IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
			[EnumeratorCancellation] CancellationToken cancellationToken = default) {

			this.RecordSpy(options);
			contextCapture?.Capture();

			if (simulateError) throw new InvalidOperationException("Simulated LLM error");

			AdditionalPropertiesDictionary metadata = new() {
				[ToolRunner.UsageMetadataKey] = new TokenUsage(100, 50, 0, 0, 0)
			};

			yield return new ChatResponseUpdate(ChatRole.Assistant, responseText) { AdditionalProperties = metadata };
			await Task.CompletedTask;
		}

		public object? GetService(Type serviceType, object? serviceKey = null) => null;

		public void Dispose() { }

		private void RecordSpy(ChatOptions? options) {
			if (spy is null) return;
			spy.ResolvedProvider = providerName;
			spy.ResolvedModelId  = options?.ModelId;
		}
	}

	private sealed class FakeUsageExtractor : IUsageExtractor {
		public TokenUsage? Extract(IReadOnlyDictionary<string, object?>? metadata) {
			if (metadata?.TryGetValue(ToolRunner.UsageMetadataKey, out object? value) == true)
				return value as TokenUsage;
			return null;
		}
	}

	private sealed class FakeApprovalService : IApprovalService {
		public Task<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct) =>
			Task.FromResult(new ApprovalResult(ApprovalDecision.Once, "auto"));

		public Task<bool> ResolveAsync(string approvalId, ApprovalDecision decision, string senderJid) =>
			Task.FromResult(true);

		public Task<bool> ResolveByMessageAsync(string messageExternalId, ApprovalDecision decision, string senderJid) =>
			Task.FromResult(true);
	}
}

internal sealed class ProviderSpy {
	public string? ResolvedProvider { get; set; }
	public string? ResolvedModelId  { get; set; }
}

internal sealed class ToolContextCapture {
	public IChannelClient? CapturedChannel              { get; set; }
	public bool            CapturedNotificationsEnabled { get; set; }
	public string?         CapturedMessageExternalId    { get; set; }
	public int             CapturedCacheBreakIndex      { get; set; }
	public int             CapturedDepth                { get; set; }

	public void Capture() {
		this.CapturedChannel              = ToolContext.Channel;
		this.CapturedNotificationsEnabled = ToolContext.NotificationsEnabled;
		this.CapturedMessageExternalId    = ToolContext.MessageExternalId;
		this.CapturedCacheBreakIndex      = ToolContext.CacheBreakIndex;
		this.CapturedDepth                = ToolContext.Depth;
	}
}

internal sealed class FakeChannel : IChannelClient {
	public int MessagesSent { get; private set; }

	public Task<string?> SendMessageAsync(string chatId, string message, string? replyToId = null, CancellationToken ct = default) {
		this.MessagesSent++;
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
}
