using Lis.Core.Channel;

using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Lis.Channels.Telegram;

/// <summary>
/// Maps a native Telegram <see cref="Message"/> to a channel-agnostic <see cref="IncomingMessage"/>.
/// Shared by the polling service and the webhook controller so both intake paths behave identically.
/// </summary>
internal static class TelegramMessageMapper {

	public static bool HasContent(Message msg) =>
		msg.Text is not null || ExtractMedia(msg) is not null;

	public static IncomingMessage Map(Message msg) {
		bool isGroup        = msg.Chat.Type is ChatType.Group or ChatType.Supergroup;
		bool isBotMentioned = false;

		if (msg.Entities is not null) {
			foreach (MessageEntity entity in msg.Entities) {
				if (entity.Type == MessageEntityType.Mention) isBotMentioned = true;
			}
		}

		(string Type, string Path)? media     = ExtractMedia(msg);
		string?                     mediaType = media?.Type;
		string?                     mediaPath = media?.Path;

		return new IncomingMessage {
			ExternalId     = msg.MessageId.ToString(),
			ChatId         = msg.Chat.Id.ToString(),
			SenderId       = msg.From?.Id.ToString() ?? "",
			SenderName     = BuildSenderName(msg.From),
			Timestamp      = new DateTimeOffset(msg.Date, TimeSpan.Zero),
			IsFromMe       = false,
			IsGroup        = isGroup,
			Body           = msg.Text,
			RepliedId      = msg.ReplyToMessage?.MessageId.ToString(),
			RepliedContent = msg.ReplyToMessage?.Text,
			IsBotMentioned = isBotMentioned,
			ChatName       = isGroup ? msg.Chat.Title : null,
			MediaType      = mediaType,
			MediaCaption   = msg.Caption,
			MediaPath      = mediaPath,
			Channel        = "telegram"
		};
	}

	/// <summary>
	/// Resolves the media kind and Telegram file id for whichever attachment the message carries.
	/// Order matters: messages like animations and video notes also populate <see cref="Message.Document"/>,
	/// so the more specific types are checked first.
	/// </summary>
	private static (string Type, string Path)? ExtractMedia(Message msg) {
		if (msg.Photo is { Length: > 0 } photo) return ("image", photo[^1].FileId);

		if (msg.Sticker is { } sticker)
			return sticker is { IsAnimated: false } and { IsVideo: false }
				? ("sticker", sticker.FileId)
				: ("document", sticker.FileId);

		if (msg.Voice is { } voice) return ("audio", voice.FileId);
		if (msg.Audio is { } audio) return ("audio", audio.FileId);
		if (msg.VideoNote is { } note) return ("video", note.FileId);
		if (msg.Video is { } video) return ("video", video.FileId);
		if (msg.Animation is { } animation) return ("video", animation.FileId);

		if (msg.Document is { } document) {
			// An image attached "as a file" arrives as a Document — surface it as an image so the model sees the bytes.
			string type = document.MimeType is { } mime && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
				? "image"
				: "document";
			return (type, document.FileId);
		}

		return null;
	}

	private static string? BuildSenderName(User? user) {
		if (user is null) return null;
		string name = user.FirstName;
		if (user.LastName is { Length: > 0 }) name += $" {user.LastName}";
		return name;
	}
}
