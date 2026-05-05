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
		if (msg.Text is null && msg.Photo is null && msg.Voice is null && msg.Document is null)
			return;

		bool isGroup       = msg.Chat.Type is ChatType.Group or ChatType.Supergroup;
		bool isBotMentioned = false;

		if (msg.Entities is not null) {
			foreach (MessageEntity entity in msg.Entities) {
				if (entity.Type == MessageEntityType.Mention)
					isBotMentioned = true;
			}
		}

		string? mediaType = null;
		string? mediaPath = null;
		if (msg.Photo is { Length: > 0 }) {
			mediaType = "image";
			mediaPath = msg.Photo[^1].FileId;
		} else if (msg.Voice is not null) {
			mediaType = "audio";
			mediaPath = msg.Voice.FileId;
		} else if (msg.Document is not null) {
			mediaType = "document";
			mediaPath = msg.Document.FileId;
		}

		IncomingMessage message = new() {
			ExternalId     = msg.MessageId.ToString(),
			ChatId         = msg.Chat.Id.ToString(),
			SenderId       = msg.From?.Id.ToString() ?? "",
			SenderName     = BuildSenderName(msg.From),
			Timestamp      = msg.Date,
			IsFromMe       = false,
			IsGroup        = isGroup,
			Body           = msg.Text ?? msg.Caption,
			RepliedId      = msg.ReplyToMessage?.MessageId.ToString(),
			RepliedContent = msg.ReplyToMessage?.Text,
			IsBotMentioned = isBotMentioned,
			ChatName       = isGroup ? msg.Chat.Title : null,
			MediaType      = mediaType,
			MediaPath      = mediaPath,
			Channel        = "telegram"
		};

		_ = Task.Run(async () => {
			try {
				await conversationService.HandleIncomingAsync(message, CancellationToken.None);
			} catch (Exception ex) {
				logger.LogError(ex, "Error processing Telegram message {MessageId}", msg.MessageId);
			}
		});
	}

	private static string? BuildSenderName(User? user) {
		if (user is null) return null;
		string name = user.FirstName;
		if (user.LastName is { Length: > 0 }) name += $" {user.LastName}";
		return name;
	}
}
