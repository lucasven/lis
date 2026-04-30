using Lis.Core.Channel;
using Lis.Core.Util;

namespace Lis.Channels.Mattermost;

public sealed class MattermostClient(MattermostApiClient api, MattermostFormatter formatter) : IChannelClient {

	[Trace("MattermostClient > SendMessageAsync")]
	public async Task<string?> SendMessageAsync(
		string chatId, string message, string? replyToId = null, CancellationToken ct = default) {

		string formatted = formatter.Format(message);
		MattermostPost? post = await api.CreatePostAsync(chatId, formatted, replyToId, ct);
		return post?.Id;
	}

	[Trace("MattermostClient > SetTypingAsync")]
	public Task SetTypingAsync(string chatId, CancellationToken ct = default) {
		// Mattermost typing indicators require WebSocket — skip for now
		return Task.CompletedTask;
	}

	[Trace("MattermostClient > StopTypingAsync")]
	public Task StopTypingAsync(string chatId, CancellationToken ct = default) {
		return Task.CompletedTask;
	}

	[Trace("MattermostClient > MarkReadAsync")]
	public Task MarkReadAsync(string messageId, string chatId, CancellationToken ct = default) {
		return Task.CompletedTask;
	}

	[Trace("MattermostClient > ReactAsync")]
	public Task ReactAsync(string messageId, string chatId, string emoji, CancellationToken ct = default) {
		return Task.CompletedTask;
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
