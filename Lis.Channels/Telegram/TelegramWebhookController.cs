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
		if (!TelegramMessageMapper.HasContent(msg))
			return this.Ok();

		IncomingMessage message = TelegramMessageMapper.Map(msg);

		_ = Task.Run(async () => {
			try {
				await conversationService.HandleIncomingAsync(message, CancellationToken.None);
			} catch (Exception ex) {
				logger.LogError(ex, "Error processing Telegram message {MessageId}", msg.MessageId);
			}
		});

		return this.Ok();
	}
}
