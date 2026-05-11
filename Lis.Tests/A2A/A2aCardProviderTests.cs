using Lis.Agent;
using Lis.Core.A2A;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Lis.Tests.A2A;

public sealed class A2aCardProviderTests : IDisposable {

	private static readonly string[] AllAgentNames = [
		"lis", "researcher", "coder", "writer", "translator",
		"summarizer", "analyst", "scheduler", "reviewer", "planner",
		"debugger", "tutor", "creative", "sysadmin", "curator",
	];

	private readonly LisDbContext _db;

	public A2aCardProviderTests() {
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
	public void ListCards_FromLis_Returns14ExcludingSelf() {
		var provider = this.CreateProvider("lis");

		IReadOnlyList<AgentCard> cards = provider.ListCards();

		Assert.Equal(14, cards.Count);
		Assert.DoesNotContain(cards, c => c.Name == "lis");
		Assert.Contains(cards, c => c.Name == "researcher");
		Assert.Contains(cards, c => c.Name == "curator");
	}

	[Fact]
	public void ListCards_FromCoder_Returns14ExcludingSelf() {
		var provider = this.CreateProvider("coder");

		IReadOnlyList<AgentCard> cards = provider.ListCards();

		Assert.Equal(14, cards.Count);
		Assert.DoesNotContain(cards, c => c.Name == "coder");
		Assert.Contains(cards, c => c.Name == "lis");
		Assert.Contains(cards, c => c.Name == "reviewer");
	}

	[Fact]
	public void ListCards_ReturnsSkillsFromPromptSections() {
		var provider = this.CreateProvider("lis");

		IReadOnlyList<AgentCard> cards = provider.ListCards();

		AgentCard coder = cards.Single(c => c.Name == "coder");
		Assert.Equal(2, coder.Skills.Count);
		Assert.Contains(coder.Skills, s => s.Name == "Code Generation");
		Assert.Contains(coder.Skills, s => s.Name == "Code Review");
	}

	[Fact]
	public void GetCard_ExistingAgent_ReturnsCard() {
		var provider = this.CreateProvider("lis");

		AgentCard card = provider.GetCard("researcher");

		Assert.Equal("researcher", card.Name);
		Assert.Equal("1.0.0", card.Version);
		Assert.Equal("1.0", card.ProtocolVersion);
		Assert.Contains("text/plain", card.DefaultInputModes);
		Assert.Single(card.Skills);
		Assert.Equal("Research", card.Skills[0].Name);
	}

	[Fact]
	public void GetCard_NonExistentAgent_ThrowsKeyNotFound() {
		var provider = this.CreateProvider("lis");

		Assert.Throws<KeyNotFoundException>(() => provider.GetCard("nonexistent"));
	}

	private A2aCardProvider CreateProvider(string callingAgent) {
		AgentEntity agent = this._db.Agents.First(a => a.Name == callingAgent);
		ToolContext.AgentId = agent.Id;

		ServiceCollection services = new();
		services.AddScoped<LisDbContext>(_ => this._db);
		ServiceProvider sp = services.BuildServiceProvider();

		return new A2aCardProvider(sp.GetRequiredService<IServiceScopeFactory>());
	}

	private void SeedAgents() {
		foreach (string name in AllAgentNames) {
			AgentEntity agent = new() {
				Name        = name,
				DisplayName = name,
				Model       = "test-model",
				CreatedAt   = DateTimeOffset.UtcNow,
				UpdatedAt   = DateTimeOffset.UtcNow,
			};
			this._db.Agents.Add(agent);
		}
		this._db.SaveChanges();

		// Add skill-tagged prompt sections to specific agents
		AgentEntity coder = this._db.Agents.First(a => a.Name == "coder");
		this._db.PromptSections.AddRange(
			new PromptSectionEntity {
				Name      = "Code Generation",
				Content   = "Generate clean, idiomatic code in any language.",
				SortOrder = 1,
				IsEnabled = true,
				Tags      = "skill",
				AgentId   = coder.Id,
				CreatedAt = DateTimeOffset.UtcNow,
				UpdatedAt = DateTimeOffset.UtcNow,
			},
			new PromptSectionEntity {
				Name      = "Code Review",
				Content   = "Review code for bugs, style, and performance issues.",
				SortOrder = 2,
				IsEnabled = true,
				Tags      = "skill",
				AgentId   = coder.Id,
				CreatedAt = DateTimeOffset.UtcNow,
				UpdatedAt = DateTimeOffset.UtcNow,
			},
			new PromptSectionEntity {
				Name      = "System Prompt",
				Content   = "You are a coding assistant.",
				SortOrder = 0,
				IsEnabled = true,
				AgentId   = coder.Id,
				CreatedAt = DateTimeOffset.UtcNow,
				UpdatedAt = DateTimeOffset.UtcNow,
			}
		);

		AgentEntity researcher = this._db.Agents.First(a => a.Name == "researcher");
		this._db.PromptSections.Add(new PromptSectionEntity {
			Name      = "Research",
			Content   = "Research topics thoroughly using available sources.",
			SortOrder = 1,
			IsEnabled = true,
			Tags      = "skill",
			AgentId   = researcher.Id,
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		});

		this._db.SaveChanges();
	}

	private sealed class TestDbContext(DbContextOptions<LisDbContext> options) : LisDbContext(options) {
		protected override void OnModelCreating(ModelBuilder modelBuilder) {
			base.OnModelCreating(modelBuilder);
			modelBuilder.Entity<MemoryEntity>().Ignore(e => e.Embedding);
			modelBuilder.Entity<SessionEntity>().Ignore(e => e.SummaryEmbedding);
		}
	}
}
