namespace Lis.Channels.Telegram;

public sealed class TelegramOptions {
	public required string BotToken { get; init; }
	public string? WebhookSecret { get; init; }
}
