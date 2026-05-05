using Lis.Core.Channel;
using Lis.Core.Util;

using Microsoft.Extensions.Logging;

using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Lis.Channels.Telegram;

public sealed class TelegramClient(TelegramBotClient bot, TelegramFormatter formatter, ILogger<TelegramClient> logger) : IChannelClient {

	[Trace("TelegramClient > SendMessageAsync")]
	public async Task<string?> SendMessageAsync(
		string chatId, string message, string? replyToId = null, CancellationToken ct = default) {

		string             formatted = formatter.Format(message);
		long               chatIdNum = long.Parse(chatId);
		ReplyParameters?   reply     = replyToId is not null
			? new ReplyParameters { MessageId = int.Parse(replyToId) }
			: null;

		try {
			Message sent = await bot.SendMessage(
				chatId: chatIdNum, text: formatted, parseMode: ParseMode.MarkdownV2,
				replyParameters: reply, cancellationToken: ct);
			return sent.MessageId.ToString();
		}
		catch (global::Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 400) {
			logger.LogWarning(ex, "MarkdownV2 formatting failed, retrying as plain text");
			Message sent = await bot.SendMessage(
				chatId: chatIdNum, text: message,
				replyParameters: reply, cancellationToken: ct);
			return sent.MessageId.ToString();
		}
	}

	[Trace("TelegramClient > SetTypingAsync")]
	public async Task SetTypingAsync(string chatId, CancellationToken ct = default) {
		await bot.SendChatAction(long.Parse(chatId), ChatAction.Typing, cancellationToken: ct);
	}

	[Trace("TelegramClient > StopTypingAsync")]
	public Task StopTypingAsync(string chatId, CancellationToken ct = default) {
		// Telegram auto-clears typing after ~5s or when a message is sent
		return Task.CompletedTask;
	}

	[Trace("TelegramClient > MarkReadAsync")]
	public Task MarkReadAsync(string messageId, string chatId, CancellationToken ct = default) {
		// Telegram Bot API doesn't support marking messages as read
		return Task.CompletedTask;
	}

	[Trace("TelegramClient > ReactAsync")]
	public async Task ReactAsync(string messageId, string chatId, string emoji, CancellationToken ct = default) {
		await bot.SetMessageReaction(
			chatId: long.Parse(chatId),
			messageId: int.Parse(messageId),
			reaction: [new ReactionTypeEmoji { Emoji = emoji }],
			cancellationToken: ct);
	}

	[Trace("TelegramClient > DownloadMediaAsync")]
	public async Task<MediaDownload?> DownloadMediaAsync(
		string messageId, string chatId, string? mediaPath = null, CancellationToken ct = default) {

		if (mediaPath is null) return null;

		TGFile file = await bot.GetFile(mediaPath, ct);
		if (file.FilePath is null) return null;

		using MemoryStream ms = new();
		await bot.DownloadFile(file, ms, ct);
		byte[] data = ms.ToArray();

		string mimeType = GuessMimeType(file.FilePath);
		return new MediaDownload(data, mimeType);
	}

	private static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
		".jpg" or ".jpeg" => "image/jpeg",
		".png"            => "image/png",
		".webp"           => "image/webp",
		".gif"            => "image/gif",
		".ogg"            => "audio/ogg",
		".mp3"            => "audio/mpeg",
		".mp4"            => "video/mp4",
		".pdf"            => "application/pdf",
		_                 => "application/octet-stream"
	};
}
