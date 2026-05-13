using Lis.Agent;
using Lis.Core.Configuration;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;
using Lis.Tools;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lis.Tests.Skills;

public sealed class PromptComposerSkillTests : IDisposable {

	private readonly LisDbContext    _db;
	private readonly PromptComposer  _composer;
	private readonly SkillPlugin     _plugin;

	public PromptComposerSkillTests() {
		DbContextOptions<LisDbContext> options = new DbContextOptionsBuilder<LisDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString())
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.Options;
		this._db = new TestDbContext(options);

		LisOptions lisOptions = new() { Timezone = "UTC" };
		this._composer = new PromptComposer(
			Options.Create(lisOptions),
			NullLogger<PromptComposer>.Instance);

		ServiceCollection services = new();
		services.AddSingleton<LisDbContext>(this._db);
		services.AddHttpClient();
		ServiceProvider sp = services.BuildServiceProvider();

		this._plugin = new SkillPlugin(
			sp.GetRequiredService<IServiceScopeFactory>(),
			sp.GetRequiredService<IHttpClientFactory>());

		this.SeedData();
		ToolContext.IsOwner = true;
	}

	public void Dispose() {
		ToolContext.AgentId = null;
		ToolContext.IsOwner = false;
		this._db.Dispose();
	}

	[Fact]
	public async Task Build_WithSkills_InsertsIndexAfterSectionsBeforeMemories() {
		AgentEntity agent = this._db.Agents.First();
		ToolContext.AgentId = agent.Id;

		string prompt = await this._composer.BuildAsync(this._db, agent.Id, CancellationToken.None);

		int sectionsEnd = prompt.IndexOf("You are a helpful assistant.", StringComparison.Ordinal);
		int skillsStart = prompt.IndexOf("Installed Skills:", StringComparison.Ordinal);
		int memoriesIdx = prompt.IndexOf("Memories:", StringComparison.Ordinal);

		Assert.True(sectionsEnd >= 0, "Sections should be present");
		Assert.True(skillsStart >= 0, "Skills index should be present");
		Assert.True(memoriesIdx >= 0, "Memories should be present");
		Assert.True(sectionsEnd < skillsStart, "Sections should come before skills");
		Assert.True(skillsStart < memoriesIdx, "Skills should come before memories");

		Assert.Contains("code-review: Reviews code for quality", prompt);
		Assert.Contains("translator: Translates messages", prompt);

		int translatorIdx  = prompt.IndexOf("code-review:", StringComparison.Ordinal);
		int codeReviewIdx  = prompt.IndexOf("translator:", StringComparison.Ordinal);
		Assert.True(translatorIdx < codeReviewIdx, "Skills should be in alphabetical order");
	}

	[Fact]
	public async Task Build_DisabledSkill_ExcludedFromIndex() {
		AgentEntity agent = this._db.Agents.First();

		string prompt = await this._composer.BuildAsync(this._db, agent.Id, CancellationToken.None);

		Assert.Contains("translator", prompt);
		Assert.DoesNotContain("disabled-skill", prompt);
	}

	[Fact]
	public async Task Use_EnabledSkill_ReturnsFullContent() {
		AgentEntity agent = this._db.Agents.First();
		ToolContext.AgentId = agent.Id;

		string result = await this._plugin.UseAsync("translator");

		Assert.Equal("When the user asks you to translate text, provide accurate translations.", result);
	}

	[Fact]
	public async Task Use_DisabledSkill_ReturnsError() {
		AgentEntity agent = this._db.Agents.First();
		ToolContext.AgentId = agent.Id;

		string result = await this._plugin.UseAsync("disabled-skill");

		Assert.Contains("disabled", result, StringComparison.OrdinalIgnoreCase);
	}

	private void SeedData() {
		AgentEntity agent = new() {
			Name      = "test-agent",
			Model     = "test-model",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		this._db.Agents.Add(agent);
		this._db.SaveChanges();

		this._db.PromptSections.Add(new PromptSectionEntity {
			Name      = "System",
			Content   = "You are a helpful assistant.",
			SortOrder = 1,
			IsEnabled = true,
			AgentId   = agent.Id,
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		});

		this._db.Skills.AddRange(
			new SkillEntity {
				Name        = "translator",
				Description = "Translates messages",
				Content     = "When the user asks you to translate text, provide accurate translations.",
				IsEnabled   = true,
				AgentId     = agent.Id,
				CreatedAt   = DateTimeOffset.UtcNow,
				UpdatedAt   = DateTimeOffset.UtcNow,
			},
			new SkillEntity {
				Name        = "code-review",
				Description = "Reviews code for quality",
				Content     = "When asked to review code, check for bugs and style issues.",
				IsEnabled   = true,
				AgentId     = agent.Id,
				CreatedAt   = DateTimeOffset.UtcNow,
				UpdatedAt   = DateTimeOffset.UtcNow,
			},
			new SkillEntity {
				Name        = "disabled-skill",
				Description = "This skill is disabled",
				Content     = "Should not appear.",
				IsEnabled   = false,
				AgentId     = agent.Id,
				CreatedAt   = DateTimeOffset.UtcNow,
				UpdatedAt   = DateTimeOffset.UtcNow,
			}
		);

		this._db.Memories.Add(new MemoryEntity {
			Content   = "User prefers concise responses",
			AgentId   = agent.Id,
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
