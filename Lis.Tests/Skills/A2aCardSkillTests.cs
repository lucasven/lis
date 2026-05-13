using Lis.Agent;
using Lis.Core.A2A;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Lis.Tests.Skills;

public sealed class A2aCardSkillTests : IDisposable {

	private readonly LisDbContext _db;

	public A2aCardSkillTests() {
		DbContextOptions<LisDbContext> options = new DbContextOptionsBuilder<LisDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString())
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.Options;
		this._db = new TestDbContext(options);

		this.SeedData();
	}

	public void Dispose() {
		ToolContext.AgentId = null;
		this._db.Dispose();
	}

	[Fact]
	public void AgentCard_IncludesEnabledSkills_ExcludesDisabled() {
		AgentEntity caller = this._db.Agents.First(a => a.Name == "caller");
		ToolContext.AgentId = caller.Id;

		ServiceCollection services = new();
		services.AddScoped<LisDbContext>(_ => this._db);
		ServiceProvider sp = services.BuildServiceProvider();

		A2aCardProvider provider = new(sp.GetRequiredService<IServiceScopeFactory>());

		AgentCard card = provider.GetCard("skilled-agent");

		Assert.Single(card.Skills);
		Assert.Equal("translator", card.Skills[0].Name);
		Assert.Equal("Translates messages", card.Skills[0].Description);
	}

	private void SeedData() {
		AgentEntity caller = new() {
			Name      = "caller",
			Model     = "test-model",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		AgentEntity skilled = new() {
			Name      = "skilled-agent",
			Model     = "test-model",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		this._db.Agents.AddRange(caller, skilled);
		this._db.SaveChanges();

		this._db.Skills.AddRange(
			new SkillEntity {
				Name        = "translator",
				Description = "Translates messages",
				Content     = "Translation instructions here.",
				IsEnabled   = true,
				AgentId     = skilled.Id,
				CreatedAt   = DateTimeOffset.UtcNow,
				UpdatedAt   = DateTimeOffset.UtcNow,
			},
			new SkillEntity {
				Name        = "disabled-skill",
				Description = "Should not appear",
				Content     = "Disabled instructions.",
				IsEnabled   = false,
				AgentId     = skilled.Id,
				CreatedAt   = DateTimeOffset.UtcNow,
				UpdatedAt   = DateTimeOffset.UtcNow,
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
