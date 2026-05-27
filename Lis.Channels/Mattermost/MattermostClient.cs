using System.Diagnostics;
using System.Text.Json.Nodes;

using Lis.Core.Channel;
using Lis.Core.Util;

using Microsoft.Extensions.Logging;

namespace Lis.Channels.Mattermost;

public sealed class MattermostClient(
	MattermostBotRegistry              registry,
	MattermostWebSocketService         wsService,
	MattermostFormatter                formatter,
	IHttpClientFactory                 httpClientFactory,
	ILogger<MattermostClient>          logger) : IChannelClient {

	[Trace("MattermostClient > SendMessageAsync")]
	public async Task<string?> SendMessageAsync(
		string chatId, string message, string? replyToId = null, CancellationToken ct = default) {

		Activity.Current?.SetTag("chat.id", chatId);
		Activity.Current?.SetTag("message.length", message.Length);

		MattermostApiClient api = this.ResolveApiClient();
		string formatted = formatter.Format(message);
		MattermostPost? post = await api.CreatePostAsync(chatId, formatted, replyToId, ct: ct);
		return post?.Id;
	}

	[Trace("MattermostClient > SetTypingAsync")]
	public async Task SetTypingAsync(string chatId, CancellationToken ct = default) {
		MattermostBotConfig? bot = this.ResolveBotConfig();
		MattermostWebSocketConnection? conn = bot is not null ? wsService.GetConnection(bot.AgentName) : null;
		if (conn is null) return;

		try {
			await conn.SendActionAsync("user_typing", new JsonObject {
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
		MattermostBotConfig? bot = this.ResolveBotConfig();
		if (bot is null) return;

		try {
			MattermostApiClient api = this.ResolveApiClient();
			await api.ViewChannelAsync(bot.UserId, chatId, ct);
		} catch (Exception ex) {
			logger.LogDebug(ex, "Failed to mark channel as read");
		}
	}

	[Trace("MattermostClient > ReactAsync")]
	public async Task ReactAsync(string messageId, string chatId, string emoji, CancellationToken ct = default) {
		MattermostBotConfig? bot = this.ResolveBotConfig();
		if (bot is null) return;

		try {
			MattermostApiClient api = this.ResolveApiClient();
			await api.CreateReactionAsync(bot.UserId, messageId, emoji, ct);
		} catch (Exception ex) {
			logger.LogDebug(ex, "Failed to add reaction");
		}
	}

	[Trace("MattermostClient > SendFileAsync")]
	public async Task<string?> SendFileAsync(
		string chatId, MediaUpload media,
		string? caption = null, string? replyToId = null, CancellationToken ct = default) {

		Activity.Current?.SetTag("chat.id", chatId);
		Activity.Current?.SetTag("file.mime_type", media.MimeType);
		Activity.Current?.SetTag("file.size", media.Data.Length);

		MattermostApiClient api = this.ResolveApiClient();

		string   filename = media.ResolveFilename();
		string[] fileIds  = await api.UploadFileAsync(chatId, filename, media.Data, media.MimeType, ct);

		MattermostPost? post = await api.CreatePostAsync(chatId, caption ?? "", replyToId, fileIds, ct);
		return post?.Id;
	}

	[Trace("MattermostClient > DownloadMediaAsync")]
	public async Task<MediaDownload?> DownloadMediaAsync(
		string messageId, string chatId, string? mediaPath = null, CancellationToken ct = default) {

		if (mediaPath is null) return null;

		MattermostApiClient api = this.ResolveApiClient();
		byte[] data = await api.GetFileAsync(mediaPath, ct);
		if (data.Length == 0) return null;

		return new MediaDownload(data, "application/octet-stream");
	}

	private MattermostBotConfig? ResolveBotConfig() {
		long? agentId = ToolContext.AgentId;
		if (agentId is > 0)
			return registry.GetByAgentId(agentId.Value);

		// Fallback to first bot
		return registry.All.Count > 0 ? registry.All[0] : null;
	}

	private MattermostApiClient ResolveApiClient() {
		MattermostBotConfig? bot = this.ResolveBotConfig();
		string clientName = bot is not null ? $"mattermost-{bot.AgentName}" : "mattermost-default";
		HttpClient http = httpClientFactory.CreateClient(clientName);
		return new MattermostApiClient(http);
	}
}
