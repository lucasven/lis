using Lis.Core.Channel;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Lis.Channels.Telegram;

[ApiController]
[Route("webhook/telegram")]
[Tags("Telegram")]
public class TelegramWebhookController(
	IConversationService               conversationService,
	IOptions<TelegramOptions>          options,
	ILogger<TelegramWebhookController> logger) : ControllerBase {

	[HttpPost]
	[ProducesResponseType(StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status401Unauthorized)]
	public async Task<IActionResult> HandleWebhook(
		[FromHeader(Name = "X-Telegram-Bot-Api-Secret-Token")] string? secretToken,
		[FromBody] Update update) {

		// Validate webhook secret
		if (options.Value.WebhookSecret is { Length: > 0 } secret && secretToken != secret)
			return this.Unauthorized();

		if (update.Type != UpdateType.Message || update.Message is null)
			return this.Ok();

		Message msg = update.Message;
		if (msg.Text is null && msg.Photo is null && msg.Voice is null && msg.Document is null)
			return this.Ok();

		bool isGroup       = msg.Chat.Type is ChatType.Group or ChatType.Supergroup;
		bool isBotMentioned = false;

		// Check for bot mention in entities
		if (msg.Entities is not null) {
			foreach (MessageEntity entity in msg.Entities) {
				if (entity.Type == MessageEntityType.Mention)
					isBotMentioned = true;
			}
		}

		// Determine media info
		string? mediaType = null;
		string? mediaPath = null;
		if (msg.Photo is { Length: > 0 }) {
			mediaType = "image";
			mediaPath = msg.Photo[^1].FileId; // Largest photo size
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

		return this.Ok();
	}

	private static string? BuildSenderName(User? user) {
		if (user is null) return null;
		string name = user.FirstName;
		if (user.LastName is { Length: > 0 }) name += $" {user.LastName}";
		return name;
	}
}
