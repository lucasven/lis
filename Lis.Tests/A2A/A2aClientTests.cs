using System.Runtime.CompilerServices;

using Lis.Agent;
using Lis.Core.A2A;
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

		ServiceCollection services = new();
		services.AddScoped<LisDbContext>(_ => this._db);
		services.AddKeyedSingleton<IChatClient>("anthropic", new FakeChatClient(cannedResponse));
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
			yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
			await Task.CompletedTask;
		}

		public object? GetService(Type serviceType, object? serviceKey = null) => null;

		public void Dispose() { }
	}
}
