# Cron/Scheduling Infrastructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Prerequisite:** Complete [Multi-Channel Foundation](2026-04-29-multi-channel-foundation.md) first (the scheduler needs to resolve the correct channel to send proactive messages).

**Goal:** Add a cron/scheduling system that lets the agent create, manage, and execute scheduled tasks — enabling proactive messaging (reminders, periodic checks, scheduled reports).

**Architecture:** DB-backed scheduled tasks with a `BackgroundService` that polls for due tasks. Each task stores: cron expression, target chat ID, channel name, agent ID, and either a prompt (AI-generated response) or a direct message. The agent gets tools (`cron_create`, `cron_list`, `cron_delete`, `cron_update`) to manage schedules. When a task fires, the system creates a synthetic `IncomingMessage` and routes it through the normal conversation pipeline, or sends a direct message via `IChannelClientProvider`.

**Tech Stack:** Cronos NuGet for cron parsing, EF Core entity, `BackgroundService`, Semantic Kernel plugin

---

### Task 1: Add Cronos NuGet Package

**Files:**
- Modify: `Lis.Tools/Lis.Tools.csproj`

- [ ] **Step 1: Add package reference**

```xml
<PackageReference Include="Cronos" Version="0.8.*" />
```

- [ ] **Step 2: Restore and build**

Run: `dotnet restore && dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Tools/Lis.Tools.csproj
git commit -m "chore(tools): add Cronos NuGet package for cron expressions

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 2: Create ScheduledTaskEntity

**Files:**
- Create: `Lis.Persistence/Entities/ScheduledTaskEntity.cs`
- Modify: `Lis.Persistence/LisDbContext.cs`

- [ ] **Step 1: Define the entity**

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lis.Persistence.Entities;

[Table("scheduled_task")]
public sealed class ScheduledTaskEntity : IEntityTypeConfiguration<ScheduledTaskEntity> {
	[Key]
	[Column("id")]
	public long Id { get; set; }

	/// <summary>Human-readable name for the task.</summary>
	[Required]
	[MaxLength(128)]
	[Column("name")]
	[JsonPropertyName("name")]
	public required string Name { get; set; }

	/// <summary>Cron expression (5-field standard or 6-field with seconds).</summary>
	[Required]
	[MaxLength(64)]
	[Column("cron_expression")]
	[JsonPropertyName("cron_expression")]
	public required string CronExpression { get; set; }

	/// <summary>IANA timezone for cron evaluation (e.g. "America/Sao_Paulo").</summary>
	[MaxLength(64)]
	[Column("timezone")]
	[JsonPropertyName("timezone")]
	public string? Timezone { get; set; }

	/// <summary>Target chat ID to send the message to.</summary>
	[Required]
	[MaxLength(128)]
	[Column("chat_id")]
	[JsonPropertyName("chat_id")]
	public required string ChatId { get; set; }

	/// <summary>Channel name (whatsapp, telegram, discord, mattermost).</summary>
	[Required]
	[MaxLength(32)]
	[Column("channel")]
	[JsonPropertyName("channel")]
	public required string Channel { get; set; }

	/// <summary>Agent ID to use for AI-generated responses (null = default agent).</summary>
	[Column("agent_id")]
	[JsonPropertyName("agent_id")]
	public long? AgentId { get; set; }

	/// <summary>
	/// What the agent should do when the task fires.
	/// If Type is "prompt", this is sent as a user message to the AI.
	/// If Type is "message", this is sent directly to the chat.
	/// </summary>
	[Required]
	[Column("payload")]
	[JsonPropertyName("payload")]
	public required string Payload { get; set; }

	/// <summary>"prompt" (AI processes it) or "message" (sent directly).</summary>
	[Required]
	[MaxLength(16)]
	[Column("type")]
	[JsonPropertyName("type")]
	public string Type { get; set; } = "prompt";

	[Column("enabled")]
	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = true;

	[Column("next_run_at")]
	[JsonPropertyName("next_run_at")]
	public DateTimeOffset? NextRunAt { get; set; }

	[Column("last_run_at")]
	[JsonPropertyName("last_run_at")]
	public DateTimeOffset? LastRunAt { get; set; }

	[Column("created_at")]
	[JsonPropertyName("created_at")]
	public DateTimeOffset CreatedAt { get; set; }

	[Column("updated_at")]
	[JsonPropertyName("updated_at")]
	public DateTimeOffset UpdatedAt { get; set; }

	public void Configure(EntityTypeBuilder<ScheduledTaskEntity> builder) {
		builder.HasIndex(e => e.Enabled);
		builder.HasIndex(e => e.NextRunAt);
	}
}
```

- [ ] **Step 2: Add DbSet to LisDbContext**

Read `Lis.Persistence/LisDbContext.cs` and add:
```csharp
public DbSet<ScheduledTaskEntity> ScheduledTasks => this.Set<ScheduledTaskEntity>();
```

- [ ] **Step 3: Create migration**

Run:
```bash
dotnet ef migrations add add_scheduled_tasks --project Lis.Persistence/Lis.Persistence.csproj --startup-project Lis.Api/Lis.Api.csproj
```

- [ ] **Step 4: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 5: Commit**

```bash
git add Lis.Persistence/Entities/ScheduledTaskEntity.cs Lis.Persistence/LisDbContext.cs Lis.Persistence/Migrations/
git commit -m "feat(persistence): add ScheduledTaskEntity for cron jobs

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 3: Create CronPlugin (Agent Tools)

**Files:**
- Create: `Lis.Tools/CronPlugin.cs`

- [ ] **Step 1: Implement the plugin**

```csharp
using System.ComponentModel;
using System.Text.Json;

using Cronos;

using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace Lis.Tools;

public sealed class CronPlugin(IServiceScopeFactory scopeFactory) {

	[KernelFunction("cron_create")]
	[Description("Create a scheduled task. Type 'prompt' sends the payload as a user message to the AI. Type 'message' sends it directly to the chat.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> CreateAsync(
		[Description("Human-readable name")] string name,
		[Description("Cron expression (e.g. '0 9 * * *' for daily at 9am, '*/5 * * * *' for every 5 minutes)")] string cronExpression,
		[Description("What to do when the task fires")] string payload,
		[Description("'prompt' (AI processes it) or 'message' (sent directly). Default: prompt")] string type = "prompt",
		[Description("IANA timezone (e.g. 'America/Sao_Paulo'). Default: UTC")] string? timezone = null) {

		// Validate cron expression
		try {
			CronExpression.Parse(cronExpression, CronFormat.Standard);
		} catch (CronFormatException ex) {
			return $"Invalid cron expression: {ex.Message}";
		}

		string chatId = ToolContext.ChatId ?? throw new InvalidOperationException("No chat context");
		string channel = ToolContext.ChannelName ?? throw new InvalidOperationException("No channel context");

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		DateTimeOffset now = DateTimeOffset.UtcNow;
		TimeZoneInfo tz = timezone is not null
			? TimeZoneInfo.FindSystemTimeZoneById(timezone)
			: TimeZoneInfo.Utc;

		CronExpression cron = CronExpression.Parse(cronExpression, CronFormat.Standard);
		DateTimeOffset? nextRun = cron.GetNextOccurrence(now, tz);

		ScheduledTaskEntity task = new() {
			Name           = name,
			CronExpression = cronExpression,
			Timezone       = timezone,
			ChatId         = chatId,
			Channel        = channel,
			AgentId        = ToolContext.AgentId,
			Payload        = payload,
			Type           = type,
			Enabled        = true,
			NextRunAt      = nextRun,
			CreatedAt      = now,
			UpdatedAt      = now,
		};

		db.ScheduledTasks.Add(task);
		await db.SaveChangesAsync();

		return $"Created scheduled task '{name}' (ID: {task.Id}). Next run: {nextRun:yyyy-MM-dd HH:mm:ss} {(timezone ?? "UTC")}.";
	}

	[KernelFunction("cron_list")]
	[Description("List all scheduled tasks for the current chat.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> ListAsync() {
		string chatId = ToolContext.ChatId ?? throw new InvalidOperationException("No chat context");

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		List<ScheduledTaskEntity> tasks = await db.ScheduledTasks
			.Where(t => t.ChatId == chatId)
			.OrderBy(t => t.Name)
			.ToListAsync();

		if (tasks.Count == 0)
			return "No scheduled tasks for this chat.";

		return JsonSerializer.Serialize(tasks.Select(t => new {
			t.Id, t.Name, t.CronExpression, t.Type, t.Enabled,
			next_run = t.NextRunAt?.ToString("yyyy-MM-dd HH:mm:ss"),
			last_run = t.LastRunAt?.ToString("yyyy-MM-dd HH:mm:ss"),
			t.Payload
		}), new JsonSerializerOptions { WriteIndented = true });
	}

	[KernelFunction("cron_delete")]
	[Description("Delete a scheduled task by ID.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> DeleteAsync(
		[Description("Task ID to delete")] long id) {

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		ScheduledTaskEntity? task = await db.ScheduledTasks.FindAsync(id);
		if (task is null) return $"Task {id} not found.";

		db.ScheduledTasks.Remove(task);
		await db.SaveChangesAsync();
		return $"Deleted task '{task.Name}' (ID: {id}).";
	}

	[KernelFunction("cron_update")]
	[Description("Update a scheduled task (enable/disable, change cron, change payload).")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> UpdateAsync(
		[Description("Task ID to update")] long id,
		[Description("New cron expression (optional)")] string? cronExpression = null,
		[Description("New payload (optional)")] string? payload = null,
		[Description("Enable or disable (optional)")] bool? enabled = null) {

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		ScheduledTaskEntity? task = await db.ScheduledTasks.FindAsync(id);
		if (task is null) return $"Task {id} not found.";

		if (cronExpression is not null) {
			try {
				CronExpression.Parse(cronExpression, CronFormat.Standard);
			} catch (CronFormatException ex) {
				return $"Invalid cron expression: {ex.Message}";
			}
			task.CronExpression = cronExpression;
		}

		if (payload is not null) task.Payload = payload;
		if (enabled is not null) task.Enabled = enabled.Value;

		// Recalculate next run
		TimeZoneInfo tz = task.Timezone is not null
			? TimeZoneInfo.FindSystemTimeZoneById(task.Timezone)
			: TimeZoneInfo.Utc;
		CronExpression cron = CronExpression.Parse(task.CronExpression, CronFormat.Standard);
		task.NextRunAt = cron.GetNextOccurrence(DateTimeOffset.UtcNow, tz);
		task.UpdatedAt = DateTimeOffset.UtcNow;

		await db.SaveChangesAsync();
		return $"Updated task '{task.Name}'. Next run: {task.NextRunAt:yyyy-MM-dd HH:mm:ss}.";
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Tools/CronPlugin.cs
git commit -m "feat(tools): add CronPlugin with create/list/delete/update

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 4: Register CronPlugin in AgentSetup

**Files:**
- Modify: `Lis.Agent/AgentSetup.cs`

- [ ] **Step 1: Read AgentSetup to find the plugin registration section**

Read the file and find where plugins are added to the kernel.

- [ ] **Step 2: Add CronPlugin registration**

Add alongside other plugins:
```csharp
kernel.Plugins.AddFromObject(new CronPlugin(scopeFactory), "cron");
```

- [ ] **Step 3: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 4: Commit**

```bash
git add Lis.Agent/AgentSetup.cs
git commit -m "feat(agent): register CronPlugin

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 5: Create CronSchedulerService (BackgroundService)

**Files:**
- Create: `Lis.Agent/CronSchedulerService.cs`

- [ ] **Step 1: Implement the background scheduler**

```csharp
using System.Diagnostics;

using Cronos;

using Lis.Core.Channel;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lis.Agent;

public sealed class CronSchedulerService(
	IServiceScopeFactory          scopeFactory,
	IChannelClientProvider        channelProvider,
	IConversationService          conversationService,
	ILogger<CronSchedulerService> logger) : BackgroundService {

	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

	[Trace("CronSchedulerService > ExecuteAsync")]
	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		logger.LogInformation("Cron scheduler started");

		while (!stoppingToken.IsCancellationRequested) {
			try {
				await this.ProcessDueTasksAsync(stoppingToken);
			} catch (Exception ex) when (ex is not OperationCanceledException) {
				logger.LogError(ex, "Error in cron scheduler loop");
			}

			await Task.Delay(PollInterval, stoppingToken);
		}
	}

	[Trace("CronSchedulerService > ProcessDueTasksAsync")]
	private async Task ProcessDueTasksAsync(CancellationToken ct) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		DateTimeOffset now = DateTimeOffset.UtcNow;

		List<ScheduledTaskEntity> dueTasks = await db.ScheduledTasks
			.Where(t => t.Enabled && t.NextRunAt != null && t.NextRunAt <= now)
			.ToListAsync(ct);

		foreach (ScheduledTaskEntity task in dueTasks) {
			Activity.Current?.SetTag("cron.task.id", task.Id);
			Activity.Current?.SetTag("cron.task.name", task.Name);

			try {
				await this.ExecuteTaskAsync(task, ct);
			} catch (Exception ex) {
				logger.LogError(ex, "Failed to execute cron task {TaskId} '{TaskName}'",
					task.Id, task.Name);
			}

			// Update next run
			TimeZoneInfo tz = task.Timezone is not null
				? TimeZoneInfo.FindSystemTimeZoneById(task.Timezone)
				: TimeZoneInfo.Utc;
			CronExpression cron = CronExpression.Parse(task.CronExpression, CronFormat.Standard);
			task.LastRunAt = now;
			task.NextRunAt = cron.GetNextOccurrence(now, tz);
			task.UpdatedAt = now;
		}

		if (dueTasks.Count > 0)
			await db.SaveChangesAsync(ct);
	}

	[Trace("CronSchedulerService > ExecuteTaskAsync")]
	private async Task ExecuteTaskAsync(ScheduledTaskEntity task, CancellationToken ct) {
		logger.LogInformation("Executing cron task {TaskId} '{TaskName}' (type: {Type})",
			task.Id, task.Name, task.Type);

		if (task.Type == "message") {
			// Direct message — send via channel client
			IChannelClient channel = channelProvider.Get(task.Channel);
			await channel.SendMessageAsync(task.ChatId, task.Payload, ct: ct);
		} else {
			// Prompt — create synthetic incoming message and process via conversation
			IncomingMessage synthetic = new() {
				ExternalId     = $"cron-{task.Id}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
				ChatId         = task.ChatId,
				SenderId       = "system:cron",
				SenderName     = $"Cron: {task.Name}",
				Timestamp      = DateTimeOffset.UtcNow,
				IsFromMe       = false,
				IsGroup        = false,
				Body           = task.Payload,
				IsBotMentioned = true, // Always respond to cron triggers
				Channel        = task.Channel
			};

			await conversationService.HandleIncomingAsync(synthetic, ct);
		}
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Agent/CronSchedulerService.cs
git commit -m "feat(agent): add CronSchedulerService BackgroundService

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 6: Register CronSchedulerService in Program.cs

**Files:**
- Modify: `Lis.Api/Program.cs`

- [ ] **Step 1: Add hosted service registration**

Inside the `if (hasChannel && hasAI)` block:
```csharp
builder.Services.AddHostedService<CronSchedulerService>();
```

- [ ] **Step 2: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Api/Program.cs
git commit -m "feat(api): register CronSchedulerService

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 7: Handle Cron Sender in Authorization

**Files:**
- Modify: `Lis.Agent/ConversationService.cs` (the ShouldRespond logic)

- [ ] **Step 1: Read ConversationService to find the ShouldRespond/auth check**

Find where `ShouldRespond` is evaluated and ensure that `senderId == "system:cron"` bypasses the sender authorization check (cron tasks are always authorized — they were created by the owner).

- [ ] **Step 2: Add cron bypass**

In the auth check section, add:
```csharp
// Cron-triggered messages are always authorized
if (message.SenderId.StartsWith("system:")) return true;
```

- [ ] **Step 3: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 4: Commit**

```bash
git add Lis.Agent/ConversationService.cs
git commit -m "feat(agent): bypass auth for system-triggered cron messages

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 8: End-to-End Verification

- [ ] **Step 1: Build and test the full solution**
- [ ] **Step 2: Run locally and verify the scheduler starts**
- [ ] **Step 3: Code cleanup with jb cleanupcode**
- [ ] **Step 4: Final commit**
