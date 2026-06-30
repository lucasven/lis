using System.Diagnostics;
using System.Text;

using Lis.Core.Channel;
using Lis.Core.Util;

using Microsoft.Extensions.Logging;

using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Lis.Channels.Telegram;

public sealed class TelegramClient(TelegramBotClient bot, TelegramFormatter formatter, ILogger<TelegramClient> logger) : IChannelClient {

	private const int MaxMessageLength = 4096;

	[Trace("TelegramClient > SendMessageAsync")]
	public async Task<string?> SendMessageAsync(
		string chatId, string message, string? replyToId = null, CancellationToken ct = default) {

		Activity.Current?.SetTag("chat.id", chatId);
		Activity.Current?.SetTag("message.length", message.Length);

		string           formatted = formatter.Format(message);
		long             chatIdNum = long.Parse(chatId);
		ReplyParameters? reply     = replyToId is not null
			? new ReplyParameters { MessageId = int.Parse(replyToId) }
			: null;

		List<string> chunks = SplitMessage(formatted);
		Activity.Current?.SetTag("message.chunks", chunks.Count);

		string? lastId = null;
		for (int i = 0; i < chunks.Count; i++) {
			try {
				Message sent = await bot.SendMessage(
					chatId: chatIdNum, text: chunks[i], parseMode: ParseMode.MarkdownV2,
					replyParameters: reply, cancellationToken: ct);
				lastId = sent.MessageId.ToString();
			}
			catch (global::Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 400) {
				logger.LogWarning(ex, "MarkdownV2 failed for chunk {Chunk}/{Total}, retrying as plain text", i + 1, chunks.Count);
				string fallback = chunks.Count == 1 ? message : chunks[i];
				Message sent = await bot.SendMessage(
					chatId: chatIdNum, text: fallback,
					replyParameters: reply, cancellationToken: ct);
				lastId = sent.MessageId.ToString();
			}
			reply = null;
		}
		return lastId;
	}

	internal static List<string> SplitMessage(string text) {
		if (text.Length <= MaxMessageLength) return [text];

		List<string>  chunks      = [];
		string[]      lines       = text.Split('\n');
		StringBuilder current     = new();
		bool          inCodeBlock = false;

		foreach (string line in lines) {
			int lineLen  = current.Length == 0 ? line.Length : 1 + line.Length;
			int overhead = inCodeBlock ? 4 : 0;

			if (current.Length > 0 && current.Length + lineLen + overhead > MaxMessageLength) {
				if (inCodeBlock) current.Append("\n```");
				chunks.Add(current.ToString());
				current.Clear();
				if (inCodeBlock) current.Append("```");
			}

			if (current.Length > 0) current.Append('\n');
			current.Append(line);

			if (line.StartsWith("```")) inCodeBlock = !inCodeBlock;
		}

		if (current.Length > 0) chunks.Add(current.ToString());
		return chunks;
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

	private const int MaxCaptionLength = 1024;

	[Trace("TelegramClient > SendFileAsync")]
	public async Task<string?> SendFileAsync(string chatId, MediaUpload media,
		string? caption = null, string? replyToId = null, CancellationToken ct = default) {

		Activity.Current?.SetTag("chat.id", chatId);
		Activity.Current?.SetTag("file.mime_type", media.MimeType);
		Activity.Current?.SetTag("file.size", media.Data.Length);

		long             chatIdNum = long.Parse(chatId);
		ReplyParameters? reply     = replyToId is not null
			? new ReplyParameters { MessageId = int.Parse(replyToId) }
			: null;

		// Captions are capped at 1024 chars; anything longer is sent as a separate follow-up message.
		string? fileCaption = caption is { Length: > MaxCaptionLength } ? null : caption;

		string    filename = media.ResolveFilename();
		using MemoryStream ms = new(media.Data);
		InputFile input = InputFile.FromStream(ms, filename);

		string mime = media.MimeType.ToLowerInvariant();
		Message sent = mime switch {
			"image/gif"                  => await bot.SendAnimation(chatIdNum, input, caption: fileCaption, replyParameters: reply, cancellationToken: ct),
			_ when mime.StartsWith("image/") => await bot.SendPhoto(chatIdNum, input, caption: fileCaption, replyParameters: reply, cancellationToken: ct),
			"audio/ogg"                  => await bot.SendVoice(chatIdNum, input, caption: fileCaption, replyParameters: reply, cancellationToken: ct),
			_ when mime.StartsWith("audio/") => await bot.SendAudio(chatIdNum, input, caption: fileCaption, replyParameters: reply, cancellationToken: ct),
			_ when mime.StartsWith("video/") => await bot.SendVideo(chatIdNum, input, caption: fileCaption, replyParameters: reply, cancellationToken: ct),
			_                            => await bot.SendDocument(chatIdNum, input, caption: fileCaption, replyParameters: reply, cancellationToken: ct)
		};

		string messageId = sent.MessageId.ToString();

		if (fileCaption is null && caption is { Length: > 0 })
			await this.SendMessageAsync(chatId, caption, messageId, ct);

		return messageId;
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
