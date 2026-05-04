namespace Lis.Channels.Mattermost;

public sealed class MattermostOptions {
	public required string BaseUrl { get; init; }
	public required string BotToken { get; init; }
	public string? BotUserId { get; init; }
}
