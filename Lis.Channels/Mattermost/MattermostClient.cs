using System.Text.Json.Nodes;

using Lis.Core.Channel;
using Lis.Core.Util;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lis.Channels.Mattermost;

public sealed class MattermostClient(
	MattermostApiClient              api,
	MattermostFormatter              formatter,
	MattermostWebSocketConnection    connection,
	IOptions<MattermostOptions>      options,
	ILogger<MattermostClient>        logger) : IChannelClient {

	[Trace("MattermostClient > SendMessageAsync")]
	public async Task<string?> SendMessageAsync(
		string chatId, string message, string? replyToId = null, CancellationToken ct = default) {

		string formatted = formatter.Format(message);
		MattermostPost? post = await api.CreatePostAsync(chatId, formatted, replyToId, ct);
		return post?.Id;
	}

	[Trace("MattermostClient > SetTypingAsync")]
	public async Task SetTypingAsync(string chatId, CancellationToken ct = default) {
		try {
			await connection.SendActionAsync("user_typing", new JsonObject {
				["channel_id"] = chatId,
				["parent_id"]  = ""
			}, ct);
		} catch (Exception ex) {
			logger.LogDebug(ex, "Failed to send typing indicator");
		}
	}

	[Trace("MattermostClient > StopTypingAsync")]
	public Task StopTypingAsync(string chatId, CancellationToken ct = default) {
		return Task.CompletedTask;
	}

	[Trace("MattermostClient > MarkReadAsync")]
	public async Task MarkReadAsync(string messageId, string chatId, CancellationToken ct = default) {
		if (options.Value.BotUserId is not { Length: > 0 } userId) return;

		try {
			await api.ViewChannelAsync(userId, chatId, ct);
		} catch (Exception ex) {
			logger.LogDebug(ex, "Failed to mark channel as read");
		}
	}

	[Trace("MattermostClient > ReactAsync")]
	public async Task ReactAsync(string messageId, string chatId, string emoji, CancellationToken ct = default) {
		if (options.Value.BotUserId is not { Length: > 0 } userId) return;

		try {
			await api.CreateReactionAsync(userId, messageId, emoji, ct);
		} catch (Exception ex) {
			logger.LogDebug(ex, "Failed to add reaction");
		}
	}

	[Trace("MattermostClient > DownloadMediaAsync")]
	public async Task<MediaDownload?> DownloadMediaAsync(
		string messageId, string chatId, string? mediaPath = null, CancellationToken ct = default) {

		if (mediaPath is null) return null;

		byte[] data = await api.GetFileAsync(mediaPath, ct);
		if (data.Length == 0) return null;

		return new MediaDownload(data, "application/octet-stream");
	}
}
