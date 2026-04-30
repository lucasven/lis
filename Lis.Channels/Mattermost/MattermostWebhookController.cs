using Lis.Channels.Mattermost.Schemas;
using Lis.Core.Channel;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lis.Channels.Mattermost;

[ApiController]
[Route("webhook/mattermost")]
[Tags("Mattermost")]
public class MattermostWebhookController(
	IConversationService                 conversationService,
	IOptions<MattermostOptions>          options,
	ILogger<MattermostWebhookController> logger) : ControllerBase {

	[HttpPost]
	[ProducesResponseType(StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status401Unauthorized)]
	public IActionResult HandleWebhook([FromBody] OutgoingWebhookPayload payload) {

		// Validate token
		if (options.Value.WebhookSecret is { Length: > 0 } secret && payload.Token != secret)
			return this.Unauthorized();

		// Skip bot's own messages
		if (options.Value.BotUserId is { Length: > 0 } botId && payload.UserId == botId)
			return this.Ok();

		if (string.IsNullOrEmpty(payload.Text))
			return this.Ok();

		// Mattermost channel types: O=public, P=private, D=direct, G=group DM
		bool isGroup = payload.ChannelName is not null
		            && !payload.ChannelName.StartsWith('D');

		// Extract file IDs for media
		string? mediaPath = payload.FileIds is { Length: > 0 }
			? payload.FileIds.Split(',')[0]
			: null;

		IncomingMessage message = new() {
			ExternalId     = payload.PostId,
			ChatId         = payload.ChannelId,
			SenderId       = payload.UserId,
			SenderName     = payload.UserName,
			Timestamp      = DateTimeOffset.FromUnixTimeSeconds(payload.Timestamp),
			IsFromMe       = false,
			IsGroup        = isGroup,
			Body           = payload.Text,
			IsBotMentioned = false,
			ChatName       = payload.ChannelName,
			MediaType      = mediaPath is not null ? "file" : null,
			MediaPath      = mediaPath,
			Channel        = "mattermost"
		};

		_ = Task.Run(async () => {
			try {
				await conversationService.HandleIncomingAsync(message, CancellationToken.None);
			} catch (Exception ex) {
				logger.LogError(ex, "Error processing Mattermost message {PostId}", payload.PostId);
			}
		});

		return this.Ok();
	}
}
