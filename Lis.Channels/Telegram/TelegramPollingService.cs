using Lis.Core.Channel;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Lis.Channels.Telegram;

public sealed class TelegramPollingService(
	TelegramBotClient                    bot,
	IConversationService                 conversationService,
	ILogger<TelegramPollingService>      logger) : BackgroundService {

	private int _offset;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		logger.LogInformation("Telegram polling started");

		while (!stoppingToken.IsCancellationRequested) {
			try {
				Update[] updates = await bot.GetUpdates(
					offset: this._offset,
					limit: 100,
					timeout: 30,
					allowedUpdates: [UpdateType.Message],
					cancellationToken: stoppingToken);

				foreach (Update update in updates) {
					this._offset = update.Id + 1;
					this.HandleUpdate(update);
				}
			} catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
				break;
			} catch (Exception ex) {
				logger.LogWarning(ex, "Telegram polling error, retrying in 5s");
				try { await Task.Delay(5000, stoppingToken); }
				catch (OperationCanceledException) { break; }
			}
		}
	}

	private void HandleUpdate(Update update) {
		if (update.Type != UpdateType.Message || update.Message is null) return;

		Message msg = update.Message;
		if (!TelegramMessageMapper.HasContent(msg)) return;

		IncomingMessage message = TelegramMessageMapper.Map(msg);

		_ = Task.Run(async () => {
			try {
				await conversationService.HandleIncomingAsync(message, CancellationToken.None);
			} catch (Exception ex) {
				logger.LogError(ex, "Error processing Telegram message {MessageId}", msg.MessageId);
			}
		});
	}
}
