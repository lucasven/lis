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
				? TimeZoneHelper.Find(task.Timezone)
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
		Activity.Current?.SetTag("cron.task.type", task.Type);

		if (task.Type == "message") {
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
				IsBotMentioned = true,
				Channel        = task.Channel,
			};

			await conversationService.HandleIncomingAsync(synthetic, ct);
		}
	}
}
