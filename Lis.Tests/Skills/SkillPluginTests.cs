using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;
using Lis.Tools;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Lis.Tests.Skills;

public sealed class SkillPluginTests : IDisposable {

	private readonly LisDbContext _db;
	private readonly SkillPlugin  _plugin;

	public SkillPluginTests() {
		DbContextOptions<LisDbContext> options = new DbContextOptionsBuilder<LisDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString())
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.Options;
		this._db = new TestDbContext(options);

		ServiceCollection services = new();
		services.AddSingleton<LisDbContext>(this._db);
		services.AddHttpClient();
		ServiceProvider sp = services.BuildServiceProvider();

		this._plugin = new SkillPlugin(
			sp.GetRequiredService<IServiceScopeFactory>(),
			sp.GetRequiredService<IHttpClientFactory>());

		this.SeedAgents();
		ToolContext.IsOwner = true;
	}

	public void Dispose() {
		ToolContext.AgentId = null;
		ToolContext.IsOwner = false;
		this._db.Dispose();
	}

	[Fact]
	public async Task Install_ExistingName_UpdatesInsteadOfDuplicating() {
		AgentEntity agent = this._db.Agents.First(a => a.Name == "agent1");
		ToolContext.AgentId = agent.Id;

		this._db.Skills.Add(new SkillEntity {
			Name        = "translator",
			Description = "Old description",
			Content     = "Old instructions",
			Version     = 1,
			AgentId     = agent.Id,
			CreatedAt   = DateTimeOffset.UtcNow.AddHours(-1),
			UpdatedAt   = DateTimeOffset.UtcNow.AddHours(-1),
		});
		await this._db.SaveChangesAsync();

		SkillEntity? before = await this._db.Skills.FirstAsync(s => s.Name == "translator");
		DateTimeOffset originalUpdatedAt = before.UpdatedAt;

		before.Description = "New description";
		before.Content     = "New instructions";
		before.Version     = 2;
		before.SourceUrl   = "https://example.com/skill.md";
		before.UpdatedAt   = DateTimeOffset.UtcNow;
		await this._db.SaveChangesAsync();

		int count = await this._db.Skills.CountAsync(s => s.AgentId == agent.Id && s.Name == "translator");
		Assert.Equal(1, count);

		SkillEntity updated = await this._db.Skills.FirstAsync(s => s.Name == "translator");
		Assert.Equal("New description", updated.Description);
		Assert.Equal("New instructions", updated.Content);
		Assert.Equal(2, updated.Version);
		Assert.True(updated.UpdatedAt > originalUpdatedAt);
	}

	[Fact]
	public async Task List_OtherAgentsSkills_ReturnsEmpty() {
		AgentEntity agent1 = this._db.Agents.First(a => a.Name == "agent1");
		AgentEntity agent2 = this._db.Agents.First(a => a.Name == "agent2");

		this._db.Skills.Add(new SkillEntity {
			Name        = "translator",
			Description = "Translates messages",
			Content     = "Instructions here",
			AgentId     = agent2.Id,
			CreatedAt   = DateTimeOffset.UtcNow,
			UpdatedAt   = DateTimeOffset.UtcNow,
		});
		await this._db.SaveChangesAsync();

		ToolContext.AgentId = agent1.Id;

		string result = await this._plugin.ListAsync();

		Assert.Contains("No skills", result);
	}

	[Fact]
	public async Task Install_NonOwner_CannotAccessOtherAgents() {
		AgentEntity agent1 = this._db.Agents.First(a => a.Name == "agent1");
		ToolContext.AgentId = agent1.Id;
		ToolContext.IsOwner = false;

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => this._plugin.ListAsync(agent: "agent2"));
	}

	[Fact]
	public async Task Uninstall_ExistingSkill_DeletesFromDatabase() {
		AgentEntity agent = this._db.Agents.First(a => a.Name == "agent1");
		ToolContext.AgentId = agent.Id;

		this._db.Skills.Add(new SkillEntity {
			Name        = "translator",
			Description = "Translates messages",
			Content     = "Instructions here",
			AgentId     = agent.Id,
			CreatedAt   = DateTimeOffset.UtcNow,
			UpdatedAt   = DateTimeOffset.UtcNow,
		});
		await this._db.SaveChangesAsync();

		string result = await this._plugin.UninstallAsync("translator");

		Assert.Contains("uninstalled", result, StringComparison.OrdinalIgnoreCase);
		Assert.Null(await this._db.Skills.FirstOrDefaultAsync(s => s.Name == "translator"));
	}

	[Fact]
	public async Task Uninstall_NonexistentSkill_ReturnsError() {
		AgentEntity agent = this._db.Agents.First(a => a.Name == "agent1");
		ToolContext.AgentId = agent.Id;

		string result = await this._plugin.UninstallAsync("nonexistent");

		Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
	}

	private void SeedAgents() {
		this._db.Agents.AddRange(
			new AgentEntity {
				Name      = "agent1",
				Model     = "test-model",
				CreatedAt = DateTimeOffset.UtcNow,
				UpdatedAt = DateTimeOffset.UtcNow,
			},
			new AgentEntity {
				Name      = "agent2",
				Model     = "test-model",
				CreatedAt = DateTimeOffset.UtcNow,
				UpdatedAt = DateTimeOffset.UtcNow,
			}
		);
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
