using System.Runtime.CompilerServices;

using Lis.Agent;
using Lis.Core.A2A;
using Lis.Core.Channel;
using Lis.Core.Configuration;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Lis.Tests.A2A;

public sealed class A2aClientTests : IDisposable {

	private static readonly string[] AllAgentNames = [
		"lis", "researcher", "coder", "writer", "translator",
		"summarizer", "analyst", "scheduler", "reviewer", "planner",
		"debugger", "tutor", "creative", "sysadmin", "curator",
	];

	private readonly LisDbContext _db;

	public A2aClientTests() {
		DbContextOptions<LisDbContext> options = new DbContextOptionsBuilder<LisDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString())
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.Options;
		this._db = new TestDbContext(options);

		this.SeedAgents();
	}

	public void Dispose() {
		ToolContext.AgentId = null;
		this._db.Dispose();
	}

	[Fact]
	public async Task SendMessage_LisToResearcher_ReturnsCompletedTask() {
		var client = this.CreateClient("lis", "The capital of France is Paris.");

		A2aMessage message = new() {
			MessageId = "test-msg-001",
			Role      = "user",
			Parts     = [new TextPart { Text = "What is the capital of France?" }],
		};

		A2aTask task = await client.SendMessageAsync("researcher", message);

		Assert.Equal(A2aTaskState.Completed, task.Status.State);
		Assert.NotNull(task.Artifacts);
		Assert.NotEmpty(task.Artifacts);

		TextPart responsePart = Assert.IsType<TextPart>(task.Artifacts[0].Parts[0]);
		Assert.False(string.IsNullOrWhiteSpace(responsePart.Text));
		Assert.NotNull(task.Id);
		Assert.NotNull(task.ContextId);
	}

	[Fact]
	public async Task SendMessage_CoderToReviewer_ReturnsCompletedTask() {
		var client = this.CreateClient("coder", "The function has a potential NullReferenceException.");

		A2aMessage message = new() {
			MessageId = "test-msg-003",
			Role      = "user",
			Parts     = [new TextPart { Text = "Review this function for null safety issues:\npublic string Format(object val) => val.ToString();" }],
		};

		A2aTask task = await client.SendMessageAsync("reviewer", message);

		Assert.Equal(A2aTaskState.Completed, task.Status.State);
		Assert.NotNull(task.Artifacts);
		Assert.NotEmpty(task.Artifacts);

		TextPart responsePart = Assert.IsType<TextPart>(task.Artifacts[0].Parts[0]);
		Assert.False(string.IsNullOrWhiteSpace(responsePart.Text));
	}

	[Fact]
	public async Task SendMessage_WithDataPart_ReturnsCompletedTask() {
		var client = this.CreateClient("lis", "Revenue shows a slight upward trend with a dip in March.");

		A2aMessage message = new() {
			MessageId = "test-msg-004",
			Role      = "user",
			Parts     = [
				new TextPart { Text = "Analyze this sales data and summarize the trend." },
				new DataPart { Data = new Dictionary<string, object> {
					["months"]   = new[] { "jan", "feb", "mar" },
					["revenue"]  = new[] { 12000, 15400, 14800 },
					["currency"] = "BRL",
				}},
			],
		};

		A2aTask task = await client.SendMessageAsync("analyst", message);

		Assert.Equal(A2aTaskState.Completed, task.Status.State);
		Assert.NotNull(task.Artifacts);
		Assert.NotEmpty(task.Artifacts);
	}

	[Fact]
	public async Task SendMessage_ResponseContainsDataPart() {
		string json = """{"slots": [{"time": "Mon 10:00"}, {"time": "Tue 14:00"}, {"time": "Wed 09:00"}]}""";
		var client = this.CreateClient("planner", json);

		A2aMessage message = new() {
			MessageId = "test-msg-006",
			Role      = "user",
			Parts     = [new TextPart { Text = "Find three available 30-minute slots this week for a team sync." }],
		};

		A2aTask task = await client.SendMessageAsync("scheduler", message);

		Assert.Equal(A2aTaskState.Completed, task.Status.State);
		Assert.NotNull(task.Artifacts);
		Assert.NotEmpty(task.Artifacts);

		List<Part> parts = task.Artifacts[0].Parts.ToList();
		Assert.Contains(parts, p => p is DataPart);

		DataPart dataPart = Assert.IsType<DataPart>(parts.First(p => p is DataPart));
		Assert.True(dataPart.Data.ContainsKey("slots"));
	}

	[Fact]
	public async Task SendMessage_NonExistentAgent_ThrowsKeyNotFound() {
		var client = this.CreateClient("lis", "");

		A2aMessage message = new() {
			MessageId = "test-msg-002",
			Role      = "user",
			Parts     = [new TextPart { Text = "Hello" }],
		};

		await Assert.ThrowsAsync<KeyNotFoundException>(
			() => client.SendMessageAsync("nonexistent", message)
		);
	}

	[Fact]
	public async Task SendMessage_EmptyParts_ThrowsInvalidOperation() {
		var client = this.CreateClient("lis", "");

		A2aMessage message = new() {
			MessageId = "test-msg-005",
			Role      = "user",
			Parts     = [],
		};

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => client.SendMessageAsync("researcher", message)
		);
	}

	private A2aClient CreateClient(string callingAgent, string cannedResponse) {
		AgentEntity agent = this._db.Agents.First(a => a.Name == callingAgent);
		ToolContext.AgentId = agent.Id;

		FakeChatClient chatClient = new(cannedResponse);

		ServiceCollection services = new();
		services.AddScoped<LisDbContext>(_ => this._db);
		services.AddKeyedSingleton<IChatClient>("anthropic", chatClient);
		services.AddKeyedSingleton<IUsageExtractor>("anthropic", new FakeUsageExtractor());

		IKernelBuilder kernelBuilder = Kernel.CreateBuilder();
		kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatClient.AsChatCompletionService());
		Kernel kernel = kernelBuilder.Build();

		ToolAuthRegistry authRegistry = new();
		authRegistry.Build(kernel);

		ToolRunner        toolRunner        = new(authRegistry, new FakeApprovalService(), NullLogger<ToolRunner>.Instance);
		ToolPolicyService toolPolicyService = new();

		services.AddSingleton(kernel);
		services.AddSingleton(toolRunner);
		services.AddSingleton(toolPolicyService);
		ServiceProvider sp = services.BuildServiceProvider();

		PromptComposer composer = new(
			Options.Create(new LisOptions()),
			NullLogger<PromptComposer>.Instance);

		return new A2aClient(
			sp.GetRequiredService<IServiceScopeFactory>(),
			sp,
			composer,
			NullLogger<A2aClient>.Instance);
	}

	private void SeedAgents() {
		foreach (string name in AllAgentNames) {
			AgentEntity agent = new() {
				Name        = name,
				DisplayName = name,
				Provider    = "anthropic",
				Model       = "test-model",
				CreatedAt   = DateTimeOffset.UtcNow,
				UpdatedAt   = DateTimeOffset.UtcNow,
			};
			this._db.Agents.Add(agent);
		}
		this._db.SaveChanges();
	}

	private sealed class TestDbContext(DbContextOptions<LisDbContext> options) : LisDbContext(options) {
		protected override void OnModelCreating(ModelBuilder modelBuilder) {
			base.OnModelCreating(modelBuilder);
			modelBuilder.Entity<MemoryEntity>().Ignore(e => e.Embedding);
			modelBuilder.Entity<SessionEntity>().Ignore(e => e.SummaryEmbedding);
		}
	}

	private sealed class FakeChatClient(string responseText) : IChatClient {
		public ChatClientMetadata Metadata { get; } = new("fake");

		public Task<ChatResponse> GetResponseAsync(
			IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) {
			ChatMessage response = new(ChatRole.Assistant, responseText);
			return Task.FromResult(new ChatResponse(response));
		}

		public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
			IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
			[EnumeratorCancellation] CancellationToken cancellationToken = default) {

			AdditionalPropertiesDictionary metadata = new() {
				[ToolRunner.UsageMetadataKey] = new TokenUsage(100, 50, 0, 0, 0)
			};

			yield return new ChatResponseUpdate(ChatRole.Assistant, responseText) { AdditionalProperties = metadata };
			await Task.CompletedTask;
		}

		public object? GetService(Type serviceType, object? serviceKey = null) => null;

		public void Dispose() { }
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
