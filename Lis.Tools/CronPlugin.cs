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

	private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

	[KernelFunction("cron_create")]
	[Description("Create a scheduled task with a cron expression (e.g. '0 9 * * 1-5' for weekdays at 9am). Type 'prompt' sends the payload as a user message to the AI for processing. Type 'message' sends it directly to the chat without AI processing. Timezone defaults to UTC unless specified.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> CreateAsync(
		[Description("Human-readable name")] string name,
		[Description("Cron expression (e.g. '0 9 * * *' for daily at 9am, '*/5 * * * *' for every 5 minutes)")] string cronExpression,
		[Description("What to do when the task fires")] string payload,
		[Description("'prompt' (AI processes it) or 'message' (sent directly). Default: prompt")] string type = "prompt",
		[Description("IANA timezone (e.g. 'America/Sao_Paulo'). Default: UTC")] string? timezone = null) {

		await ToolContext.NotifyAsync("⏰ Creating scheduled task");

		try {
			CronExpression.Parse(cronExpression, CronFormat.Standard);
		} catch (CronFormatException ex) {
			return $"Invalid cron expression: {ex.Message}";
		}

		string chatId  = ToolContext.ChatId ?? throw new InvalidOperationException("No chat context");
		string channel = ToolContext.ChannelName ?? throw new InvalidOperationException("No channel context");

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		DateTimeOffset now = DateTimeOffset.UtcNow;
		TimeZoneInfo tz = timezone is not null
			? TimeZoneHelper.Find(timezone)
			: TimeZoneInfo.Utc;

		CronExpression cron    = CronExpression.Parse(cronExpression, CronFormat.Standard);
		DateTimeOffset? nextRun = cron.GetNextOccurrence(now, tz);

		ScheduledTaskEntity task = new() {
			Name            = name,
			CronExpression  = cronExpression,
			Timezone        = timezone,
			ChatId          = chatId,
			Channel         = channel,
			AgentId         = ToolContext.AgentId,
			CreatorSenderId = ToolContext.SenderJid,
			Payload         = payload,
			Type            = type,
			Enabled         = true,
			NextRunAt       = nextRun,
			CreatedAt       = now,
			UpdatedAt       = now,
		};

		db.ScheduledTasks.Add(task);
		await db.SaveChangesAsync();

		return $"Created scheduled task '{name}' (ID: {task.Id}). Next run: {nextRun:yyyy-MM-dd HH:mm:ss} {timezone ?? "UTC"}.";
	}

	[KernelFunction("cron_list")]
	[Description("List all scheduled tasks for the current chat with their ID, cron expression, type, enabled status, and next run time.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> ListAsync() {
		await ToolContext.NotifyAsync("📋 Listing scheduled tasks");

		string chatId = ToolContext.ChatId ?? throw new InvalidOperationException("No chat context");

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		List<ScheduledTaskEntity> tasks = await db.ScheduledTasks
			.Where(t => t.ChatId == chatId)
			.OrderBy(t => t.Name)
			.ToListAsync();

		if (tasks.Count == 0) return "No scheduled tasks for this chat.";

		return JsonSerializer.Serialize(tasks.Select(t => new {
			t.Id, t.Name, t.CronExpression, t.Type, t.Enabled,
			next_run = t.NextRunAt?.ToString("yyyy-MM-dd HH:mm:ss"),
			last_run = t.LastRunAt?.ToString("yyyy-MM-dd HH:mm:ss"),
			t.Payload,
		}), JsonOpts);
	}

	[KernelFunction("cron_delete")]
	[Description("Delete a scheduled task by its numeric ID. Use cron-cron_list to find task IDs.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> DeleteAsync(
		[Description("Task ID to delete")] long id) {

		await ToolContext.NotifyAsync("🗑️ Deleting scheduled task");

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		ScheduledTaskEntity? task = await db.ScheduledTasks.FindAsync(id);
		if (task is null) return $"Task {id} not found.";

		db.ScheduledTasks.Remove(task);
		await db.SaveChangesAsync();

		return $"Deleted task '{task.Name}' (ID: {id}).";
	}

	[KernelFunction("cron_update")]
	[Description("Update a scheduled task by ID. Can change cron expression, payload text, or toggle enabled/disabled. Use cron-cron_list to find task IDs and current values.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> UpdateAsync(
		[Description("Task ID to update")] long id,
		[Description("New cron expression (optional)")] string? cronExpression = null,
		[Description("New payload (optional)")] string? payload = null,
		[Description("Enable or disable (optional)")] bool? enabled = null) {

		await ToolContext.NotifyAsync("✏️ Updating scheduled task");

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

		TimeZoneInfo tz = task.Timezone is not null
			? TimeZoneHelper.Find(task.Timezone)
			: TimeZoneInfo.Utc;
		CronExpression cron = CronExpression.Parse(task.CronExpression, CronFormat.Standard);
		task.NextRunAt = cron.GetNextOccurrence(DateTimeOffset.UtcNow, tz);
		task.UpdatedAt = DateTimeOffset.UtcNow;

		await db.SaveChangesAsync();

		return $"Updated task '{task.Name}'. Next run: {task.NextRunAt:yyyy-MM-dd HH:mm:ss}.";
	}
}
