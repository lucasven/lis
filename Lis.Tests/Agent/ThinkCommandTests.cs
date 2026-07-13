using Lis.Agent.Commands;
using Lis.Core.Channel;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lis.Tests.Agent;

public class ThinkCommandTests : IDisposable {
	private readonly LisDbContext _db;
	private readonly AgentEntity  _agent;
	private readonly ThinkCommand _sut = new();

	public ThinkCommandTests() {
		DbContextOptions<LisDbContext> options = new DbContextOptionsBuilder<LisDbContext>()
			.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.Options;
		this._db = new TestDbContext(options);

		this._agent = new AgentEntity {
			Name      = "default",
			Model     = "claude-opus-4-8",
			IsDefault = true,
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow
		};
		this._db.Agents.Add(this._agent);
		this._db.SaveChanges();
	}

	public void Dispose() {
		this._db.Dispose();
		GC.SuppressFinalize(this);
	}

	private CommandContext Ctx(string? args) {
		ChatEntity chat = new() { ExternalId = "chat", AgentId = this._agent.Id };
		IncomingMessage msg = new() {
			ExternalId = "m1", ChatId = "chat", SenderId = "owner", Body = "/think",
			Channel    = "test"
		};
		return new CommandContext(msg, chat, Session: null, this._db, this._agent, args);
	}

	[Theory]
	[InlineData("low")]
	[InlineData("medium")]
	[InlineData("high")]
	[InlineData("xhigh")]
	[InlineData("max")]
	[InlineData("8192")]
	public async Task Sets_valid_effort_on_the_agent(string effort) {
		string reply = await this._sut.ExecuteAsync(this.Ctx(effort), CancellationToken.None);

		Assert.StartsWith("✅", reply);
		Assert.Equal(effort, this._db.Agents.Single().ThinkingEffort);
	}

	[Theory]
	[InlineData("off")]
	[InlineData("none")]
	[InlineData("disable")]
	public async Task Disables_thinking(string arg) {
		this._agent.ThinkingEffort = "high";
		await this._db.SaveChangesAsync();

		string reply = await this._sut.ExecuteAsync(this.Ctx(arg), CancellationToken.None);

		Assert.StartsWith("✅", reply);
		Assert.Null(this._db.Agents.Single().ThinkingEffort);
	}

	[Theory]
	[InlineData("bogus")]
	[InlineData("100")] // below the 1024 budget minimum
	[InlineData("-5")]
	public async Task Rejects_invalid_effort(string arg) {
		this._agent.ThinkingEffort = "high";
		await this._db.SaveChangesAsync();

		string reply = await this._sut.ExecuteAsync(this.Ctx(arg), CancellationToken.None);

		Assert.StartsWith("⚠️", reply);
		Assert.Equal("high", this._db.Agents.Single().ThinkingEffort);
	}

	[Fact]
	public async Task Rejects_xhigh_on_non_adaptive_model() {
		this._agent.Model = "claude-sonnet-4-20250514";
		await this._db.SaveChangesAsync();

		string reply = await this._sut.ExecuteAsync(this.Ctx("xhigh"), CancellationToken.None);

		Assert.StartsWith("⚠️", reply);
		Assert.Null(this._db.Agents.Single().ThinkingEffort);
	}

	[Fact]
	public async Task Without_args_shows_current_state() {
		Assert.Contains("off", await this._sut.ExecuteAsync(this.Ctx(null), CancellationToken.None));

		this._agent.ThinkingEffort = "xhigh";
		await this._db.SaveChangesAsync();
		Assert.Contains("xhigh", await this._sut.ExecuteAsync(this.Ctx(null), CancellationToken.None));
	}

	private sealed class TestDbContext(DbContextOptions<LisDbContext> options) : LisDbContext(options) {
		protected override void OnModelCreating(ModelBuilder modelBuilder) {
			base.OnModelCreating(modelBuilder);
			modelBuilder.Entity<MemoryEntity>().Ignore(e => e.Embedding);
			modelBuilder.Entity<SessionEntity>().Ignore(e => e.SummaryEmbedding);
		}
	}
}
