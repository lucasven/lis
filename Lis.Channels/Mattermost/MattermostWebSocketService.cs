using System.Diagnostics;
using System.Text.Json;

using Lis.Channels.Mattermost.Schemas;
using Lis.Core.Channel;
using Lis.Core.Util;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lis.Channels.Mattermost;

public sealed class MattermostWebSocketService(
	MattermostWebSocketConnection            connection,
	IConversationService                     conversationService,
	IOptions<MattermostOptions>              options,
	ILogger<MattermostWebSocketService>      logger) : BackgroundService {

	private static readonly TimeSpan InitialBackoff    = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan MaxBackoff        = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan HeartbeatTimeout  = TimeSpan.FromSeconds(60);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		TimeSpan backoff = InitialBackoff;

		while (!stoppingToken.IsCancellationRequested) {
			try {
				await connection.ConnectAsync(stoppingToken);
				backoff = InitialBackoff;
				await this.ListenLoopAsync(stoppingToken);
			} catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
				break;
			} catch (Exception ex) {
				logger.LogWarning(ex, "WebSocket disconnected, reconnecting in {Backoff}s", backoff.TotalSeconds);
			}

			try {
				await Task.Delay(backoff, stoppingToken);
			} catch (OperationCanceledException) {
				break;
			}

			backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
		}

		await connection.DisconnectAsync();
	}

	[Trace("MattermostWebSocketService > ListenLoopAsync")]
	private async Task ListenLoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested && connection.IsConnected) {
			using CancellationTokenSource heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
			heartbeat.CancelAfter(HeartbeatTimeout);

			JsonDocument? msg;
			try {
				msg = await connection.ReceiveAsync(heartbeat.Token);
			} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
				logger.LogWarning("No message received in {Timeout}s, reconnecting", HeartbeatTimeout.TotalSeconds);
				return;
			}

			if (msg is null) return;

			using (msg) {
				string? eventType = msg.RootElement.TryGetProperty("event", out JsonElement e)
					? e.GetString()
					: null;

				if (eventType is null) continue;

				try {
					switch (eventType) {
						case "posted":
							this.HandlePosted(msg.RootElement);
							break;
						case "reaction_added":
							this.HandleReactionAdded(msg.RootElement);
							break;
					}
				} catch (Exception ex) {
					logger.LogError(ex, "Error handling {EventType} event", eventType);
				}
			}
		}
	}

	private void HandlePosted(JsonElement root) {
		if (!root.TryGetProperty("data", out JsonElement data)) return;

		string? postJson = data.TryGetProperty("post", out JsonElement postEl)
			? postEl.GetString()
			: null;
		if (postJson is null) return;

		WsPost? post = JsonSerializer.Deserialize<WsPost>(postJson);
		if (post is null) return;

		if (options.Value.BotUserId is { Length: > 0 } botId && post.UserId == botId)
			return;

		if (string.IsNullOrEmpty(post.Message) && post.FileIds is not { Length: > 0 })
			return;

		string? channelType = data.TryGetProperty("channel_type", out JsonElement ctEl)
			? ctEl.GetString()
			: null;
		string? channelName = data.TryGetProperty("channel_display_name", out JsonElement cn)
			? cn.GetString()
			: null;
		string? senderName = data.TryGetProperty("sender_name", out JsonElement sn)
			? sn.GetString()?.TrimStart('@')
			: null;

		bool isBotMentioned = false;
		if (options.Value.BotUserId is { Length: > 0 } botUid
		    && data.TryGetProperty("mentions", out JsonElement mentionsEl)) {
			string? mentionsJson = mentionsEl.GetString();
			if (mentionsJson is not null) {
				string[]? mentions = JsonSerializer.Deserialize<string[]>(mentionsJson);
				isBotMentioned = mentions?.Contains(botUid) == true;
			}
		}

		bool    isGroup   = channelType is not "D";
		string? mediaPath = post.FileIds is { Length: > 0 } ? post.FileIds[0] : null;

		IncomingMessage message = new() {
			ExternalId     = post.Id,
			ChatId         = post.ChannelId,
			SenderId       = post.UserId,
			SenderName     = senderName,
			Timestamp      = DateTimeOffset.FromUnixTimeMilliseconds(post.CreateAt),
			IsFromMe       = false,
			IsGroup        = isGroup,
			Body           = post.Message,
			RepliedId      = string.IsNullOrEmpty(post.RootId) ? null : post.RootId,
			IsBotMentioned = isBotMentioned,
			ChatName       = channelName,
			MediaType      = mediaPath is not null ? "file" : null,
			MediaPath      = mediaPath,
			Channel        = "mattermost"
		};

		Activity.Current?.SetTag("message.id", post.Id);
		Activity.Current?.SetTag("chat.id", post.ChannelId);

		_ = Task.Run(async () => {
			try {
				await conversationService.HandleIncomingAsync(message, CancellationToken.None);
			} catch (Exception ex) {
				logger.LogError(ex, "Error processing Mattermost message {PostId}", post.Id);
			}
		});
	}

	private void HandleReactionAdded(JsonElement root) {
		if (!root.TryGetProperty("data", out JsonElement data)) return;

		string? reactionJson = data.TryGetProperty("reaction", out JsonElement re)
			? re.GetString()
			: null;
		if (reactionJson is null) return;

		WsReaction? reaction = JsonSerializer.Deserialize<WsReaction>(reactionJson);
		if (reaction is null) return;

		if (options.Value.BotUserId is { Length: > 0 } botId && reaction.UserId == botId)
			return;

		string? channelId = root.TryGetProperty("broadcast", out JsonElement bc)
		                    && bc.TryGetProperty("channel_id", out JsonElement ch)
			? ch.GetString()
			: null;
		if (channelId is null) return;

		_ = Task.Run(async () => {
			try {
				await conversationService.HandleReactionAsync(
					reaction.PostId, channelId, reaction.EmojiName, reaction.UserId, CancellationToken.None);
			} catch (Exception ex) {
				logger.LogError(ex, "Error processing Mattermost reaction on {PostId}", reaction.PostId);
			}
		});
	}
}
