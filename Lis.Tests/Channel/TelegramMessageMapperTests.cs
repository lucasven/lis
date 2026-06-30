using Lis.Channels.Telegram;
using Lis.Core.Channel;

using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Lis.Tests.Channel;

public class TelegramMessageMapperTests {

	private static Message Base(Action<Message> configure) {
		Message msg = new() {
			Id   = 1,
			Date = DateTime.UtcNow,
			Chat = new Chat { Id = 42, Type = ChatType.Private }
		};
		configure(msg);
		return msg;
	}

	[Fact]
	public void HasContent_TextOnly_True() {
		Assert.True(TelegramMessageMapper.HasContent(Base(m => m.Text = "hi")));
	}

	[Fact]
	public void HasContent_Empty_False() {
		Assert.False(TelegramMessageMapper.HasContent(Base(_ => { })));
	}

	[Fact]
	public void HasContent_Video_True() {
		Assert.True(TelegramMessageMapper.HasContent(Base(m => m.Video = new Video { FileId = "v" })));
	}

	[Fact]
	public void Map_Photo_ClassifiesAsImage_UsesLargest() {
		IncomingMessage result = TelegramMessageMapper.Map(Base(m => m.Photo = [
			new PhotoSize { FileId = "small" },
			new PhotoSize { FileId = "large" }
		]));

		Assert.Equal("image", result.MediaType);
		Assert.Equal("large", result.MediaPath);
	}

	[Fact]
	public void Map_ImageDocument_ClassifiesAsImage() {
		IncomingMessage result = TelegramMessageMapper.Map(Base(m =>
			m.Document = new Document { FileId = "doc", MimeType = "image/png" }));

		Assert.Equal("image", result.MediaType);
		Assert.Equal("doc", result.MediaPath);
	}

	[Fact]
	public void Map_NonImageDocument_ClassifiesAsDocument() {
		IncomingMessage result = TelegramMessageMapper.Map(Base(m =>
			m.Document = new Document { FileId = "pdf", MimeType = "application/pdf" }));

		Assert.Equal("document", result.MediaType);
	}

	[Fact]
	public void Map_Voice_ClassifiesAsAudio() {
		IncomingMessage result = TelegramMessageMapper.Map(Base(m => m.Voice = new Voice { FileId = "voice" }));
		Assert.Equal("audio", result.MediaType);
	}

	[Fact]
	public void Map_Audio_ClassifiesAsAudio() {
		IncomingMessage result = TelegramMessageMapper.Map(Base(m => m.Audio = new Audio { FileId = "audio" }));
		Assert.Equal("audio", result.MediaType);
	}

	[Fact]
	public void Map_Video_ClassifiesAsVideo() {
		IncomingMessage result = TelegramMessageMapper.Map(Base(m => m.Video = new Video { FileId = "video" }));
		Assert.Equal("video", result.MediaType);
	}

	[Fact]
	public void Map_StaticSticker_ClassifiesAsSticker() {
		IncomingMessage result = TelegramMessageMapper.Map(Base(m =>
			m.Sticker = new Sticker { FileId = "s", IsAnimated = false, IsVideo = false }));
		Assert.Equal("sticker", result.MediaType);
	}

	[Fact]
	public void Map_AnimatedSticker_ClassifiesAsDocument() {
		IncomingMessage result = TelegramMessageMapper.Map(Base(m =>
			m.Sticker = new Sticker { FileId = "s", IsAnimated = true, IsVideo = false }));
		Assert.Equal("document", result.MediaType);
	}

	[Fact]
	public void Map_CaptionWithMedia_PreservesCaption() {
		IncomingMessage result = TelegramMessageMapper.Map(Base(m => {
			m.Document = new Document { FileId = "doc", MimeType = "image/png" };
			m.Caption  = "look at this";
		}));

		Assert.Equal("image", result.MediaType);
		Assert.Equal("look at this", result.MediaCaption);
	}
}
