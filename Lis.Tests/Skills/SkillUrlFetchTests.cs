using System.Net;

using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;
using Lis.Tools;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Lis.Tests.Skills;

public sealed class SkillUrlFetchTests : IDisposable {

	private readonly LisDbContext _db;

	public SkillUrlFetchTests() {
		DbContextOptions<LisDbContext> options = new DbContextOptionsBuilder<LisDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString())
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.Options;
		this._db = new TestDbContext(options);

		AgentEntity agent = new() {
			Name      = "test-agent",
			Model     = "test-model",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		this._db.Agents.Add(agent);
		this._db.SaveChanges();

		ToolContext.AgentId = agent.Id;
		ToolContext.IsOwner = true;
	}

	public void Dispose() {
		ToolContext.AgentId = null;
		ToolContext.IsOwner = false;
		this._db.Dispose();
	}

	[Fact]
	public async Task Validate_NonHttpUrl_Rejected() {
		SkillPlugin plugin = this.CreatePlugin();

		string result = await plugin.InstallAsync("ftp://example.com/skill.md");

		Assert.Contains("HTTP", result, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Fetch_404Response_ReturnsError() {
		SkillPlugin plugin = this.CreatePlugin(HttpStatusCode.NotFound);

		string result = await plugin.InstallAsync("https://example.com/skill.md");

		Assert.Contains("404", result);
	}

	private SkillPlugin CreatePlugin(HttpStatusCode? statusCode = null) {
		ServiceCollection services = new();
		services.AddScoped<LisDbContext>(_ => this._db);

		if (statusCode is not null) {
			services.AddHttpClient()
				.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(
					() => new FakeHandler(statusCode.Value)));
		}
		else {
			services.AddHttpClient();
		}

		ServiceProvider sp = services.BuildServiceProvider();

		return new SkillPlugin(
			sp.GetRequiredService<IServiceScopeFactory>(),
			sp.GetRequiredService<IHttpClientFactory>());
	}

	private sealed class FakeHandler(HttpStatusCode statusCode) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken) {
			return Task.FromResult(new HttpResponseMessage(statusCode));
		}
	}

	private sealed class TestDbContext(DbContextOptions<LisDbContext> options) : LisDbContext(options) {
		protected override void OnModelCreating(ModelBuilder modelBuilder) {
			base.OnModelCreating(modelBuilder);
			modelBuilder.Entity<MemoryEntity>().Ignore(e => e.Embedding);
			modelBuilder.Entity<SessionEntity>().Ignore(e => e.SummaryEmbedding);
		}
	}
}
