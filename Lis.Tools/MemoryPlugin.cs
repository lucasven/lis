using System.ComponentModel;
using System.Text;

using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Lis.Tools;

public sealed class MemoryPlugin(IServiceScopeFactory scopeFactory) {

	[KernelFunction("create_memory")]
	[Description("Store a new memory. Optionally link to a person by name.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public async Task<string> CreateMemoryAsync(
		[Description("The information to remember")] string content,
		[Description("Person's name this memory is about (optional)")] string? contactName = null) {
		await ToolContext.NotifyAsync($"💾 Saving memory{(contactName is not null ? $"\ncontact: {contactName}" : "")}\n```\n{content}\n```");
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		long? contactId = await ResolveOrCreateContactAsync(db, contactName);
		Vector? embedding = await GenerateEmbeddingAsync(scope.ServiceProvider, content);

		MemoryEntity memory = new() {
			Content   = content.Trim(),
			AgentId   = ToolContext.AgentId,
			ContactId = contactId,
			Embedding = embedding,
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};

		db.Memories.Add(memory);
		await db.SaveChangesAsync();

		return $"Memory #{memory.Id} saved.";
	}

	[KernelFunction("search_memories")]
	[Description("Search stored memories by keyword or phrase. Optionally filter by person.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public async Task<string> SearchMemoriesAsync(
		[Description("Search keyword or phrase")] string query,
		[Description("Person's name to filter by (optional)")] string? contactName = null) {
		await ToolContext.NotifyAsync($"🔍 Searching memories\nquery: {query}{(contactName is not null ? $"\ncontact: {contactName}" : "")}");
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		long? contactId = null;
		if (!string.IsNullOrWhiteSpace(contactName)) {
			ContactEntity? contact = await db.Contacts
				.FirstOrDefaultAsync(c => EF.Functions.ILike(c.Name!, contactName));
			contactId = contact?.Id;

			if (contactId is null) return $"No contact found named '{contactName}'.";
		}

		SearchParams p = new(
			AgentId:             null,
			ContactId:           contactId,
			After:               null,
			Before:              null,
			Limit:               10,
			Offset:              0,
			ScopeToCurrentAgent: true
		);

		List<MemoryEntity> results = await SearchAsync(scope.ServiceProvider, db, query, p);
		if (results.Count == 0) return "No memories found.";

		StringBuilder sb = new();
		foreach (MemoryEntity mem in results) {
			string prefix = mem.Contact is not null ? $"[{mem.Contact.Name}] " : "";
			sb.AppendLine($"#{mem.Id}: {prefix}{mem.Content}");
		}

		return sb.ToString().TrimEnd();
	}

	[KernelFunction("search_agent_memories")]
	[Description("Search memories across all agents. Optionally filter by agent, person, date range, with pagination.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public async Task<string> SearchAgentMemoriesAsync(
		[Description("Search keyword or phrase")] string query,
		[Description("Agent name to filter by (optional)")] string? agentName = null,
		[Description("Person's name to filter by (optional)")] string? contactName = null,
		[Description("Only memories created on or after this date (optional)")] DateTimeOffset? after = null,
		[Description("Only memories created on or before this date (optional)")] DateTimeOffset? before = null,
		[Description("Max results to return, 1-100 (default 10)")] int limit = 10,
		[Description("Number of results to skip for pagination (default 0)")] int offset = 0) {
		await ToolContext.NotifyAsync($"🔍 Searching agent memories\nquery: {query}"
			+ (agentName is not null ? $"\nagent: {agentName}" : "")
			+ (contactName is not null ? $"\ncontact: {contactName}" : ""));

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		long? agentId = null;
		if (!string.IsNullOrWhiteSpace(agentName)) {
			AgentEntity? agent = await db.Agents
				.FirstOrDefaultAsync(a => EF.Functions.ILike(a.Name, agentName));

			if (agent is null) return $"No agent found named '{agentName}'.";
			agentId = agent.Id;
		}

		long? contactId = null;
		if (!string.IsNullOrWhiteSpace(contactName)) {
			ContactEntity? contact = await db.Contacts
				.FirstOrDefaultAsync(c => EF.Functions.ILike(c.Name!, contactName));

			if (contact is null) return $"No contact found named '{contactName}'.";
			contactId = contact.Id;
		}

		limit = Math.Clamp(limit, 1, 100);
		if (offset < 0) offset = 0;

		SearchParams p = new(
			AgentId:             agentId,
			ContactId:           contactId,
			After:               after,
			Before:              before,
			Limit:               limit,
			Offset:              offset,
			ScopeToCurrentAgent: false
		);

		List<MemoryEntity> results = await SearchAsync(scope.ServiceProvider, db, query, p);
		if (results.Count == 0) return "No memories found.";

		StringBuilder sb = new();
		foreach (MemoryEntity mem in results) {
			string agentPrefix   = mem.Agent is not null ? $"[{mem.Agent.Name}] " : "";
			string contactPrefix = mem.Contact is not null ? $"[{mem.Contact.Name}] " : "";
			sb.AppendLine($"#{mem.Id}: {agentPrefix}{contactPrefix}{mem.Content}");
		}

		return sb.ToString().TrimEnd();
	}

	[KernelFunction("update_memory")]
	[Description("Update an existing memory's content.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public async Task<string> UpdateMemoryAsync(
		[Description("Memory ID")] long id,
		[Description("Updated content")] string content) {
		await ToolContext.NotifyAsync($"✏️ Updating memory #{id}\n```\n{content}\n```");
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		MemoryEntity? memory = await db.Memories.FindAsync(id);
		if (memory is null) return $"Memory #{id} not found.";

		memory.Content   = content.Trim();
		memory.Embedding = await GenerateEmbeddingAsync(scope.ServiceProvider, content);
		memory.UpdatedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();

		return $"Memory #{id} updated.";
	}

	[KernelFunction("delete_memory")]
	[Description("Delete a memory by ID.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public async Task<string> DeleteMemoryAsync(
		[Description("Memory ID")] long id) {
		await ToolContext.NotifyAsync($"🗑️ Deleting memory #{id}");
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		MemoryEntity? memory = await db.Memories.FindAsync(id);
		if (memory is null) return $"Memory #{id} not found.";

		db.Memories.Remove(memory);
		await db.SaveChangesAsync();

		return $"Memory #{id} deleted.";
	}

	private static async Task<long?> ResolveOrCreateContactAsync(LisDbContext db, string? contactName) {
		if (string.IsNullOrWhiteSpace(contactName)) return null;

		ContactEntity? contact = await db.Contacts
			.FirstOrDefaultAsync(c => EF.Functions.ILike(c.Name!, contactName));

		if (contact is not null) return contact.Id;

		contact = new ContactEntity {
			Name      = contactName.Trim(),
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};

		db.Contacts.Add(contact);
		await db.SaveChangesAsync();

		return contact.Id;
	}

	private static async Task<Vector?> GenerateEmbeddingAsync(IServiceProvider sp, string content) {
		IEmbeddingGenerator<string, Embedding<float>>? embeddingGen =
			sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();

		if (embeddingGen is null) return null;

		GeneratedEmbeddings<Embedding<float>> result = await embeddingGen.GenerateAsync([content]);
		return new Vector(result[0].Vector);
	}

	private static async Task<List<MemoryEntity>> SearchAsync(
		IServiceProvider sp, LisDbContext db, string query, SearchParams p) {

		IEmbeddingGenerator<string, Embedding<float>>? embeddingGen =
			sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();

		return embeddingGen is not null
			? await VectorSearchAsync(db, embeddingGen, query, p)
			: await FtsSearchAsync(db, query, p);
	}

	private sealed record SearchParams(
		long?           AgentId,
		long?           ContactId,
		DateTimeOffset? After,
		DateTimeOffset? Before,
		int             Limit,
		int             Offset,
		bool            ScopeToCurrentAgent
	);

	private static IQueryable<MemoryEntity> ApplyFilters(IQueryable<MemoryEntity> q, SearchParams p) {
		if (p.ScopeToCurrentAgent) {
			long? agentId = ToolContext.AgentId;
			if (agentId is > 0)
				q = q.Where(m => m.AgentId == agentId || m.AgentId == null);
		} else if (p.AgentId is not null) {
			q = q.Where(m => m.AgentId == p.AgentId);
		}

		if (p.ContactId is not null) q = q.Where(m => m.ContactId == p.ContactId);
		if (p.After is not null)     q = q.Where(m => m.CreatedAt >= p.After);
		if (p.Before is not null)    q = q.Where(m => m.CreatedAt <= p.Before);

		return q;
	}

	private static async Task<List<MemoryEntity>> VectorSearchAsync(
		LisDbContext db,
		IEmbeddingGenerator<string, Embedding<float>> embeddingGen,
		string query,
		SearchParams p) {

		GeneratedEmbeddings<Embedding<float>> result = await embeddingGen.GenerateAsync([query]);
		Vector queryVector = new(result[0].Vector);

		IQueryable<MemoryEntity> q = db.Memories
			.Include(m => m.Agent)
			.Include(m => m.Contact)
			.Where(m => m.Embedding != null);

		q = ApplyFilters(q, p);

		return await q
			.OrderBy(m => m.Embedding!.CosineDistance(queryVector))
			.Skip(p.Offset)
			.Take(p.Limit)
			.ToListAsync();
	}

	private static async Task<List<MemoryEntity>> FtsSearchAsync(
		LisDbContext db,
		string query,
		SearchParams p) {

		IQueryable<MemoryEntity> q = db.Memories
			.Include(m => m.Agent)
			.Include(m => m.Contact);

		q = ApplyFilters(q, p);

		return await q
			.Where(m => EF.Functions.ToTsVector("simple", m.Content)
				.Matches(EF.Functions.WebSearchToTsQuery("simple", query)))
			.OrderByDescending(m => EF.Functions.ToTsVector("simple", m.Content)
				.Rank(EF.Functions.WebSearchToTsQuery("simple", query)))
			.Skip(p.Offset)
			.Take(p.Limit)
			.ToListAsync();
	}
}
