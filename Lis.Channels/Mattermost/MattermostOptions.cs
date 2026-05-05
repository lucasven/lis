namespace Lis.Channels.Mattermost;

public sealed class MattermostOptions {
	public required string BaseUrl { get; init; }
	public required List<MattermostBotConfig> Bots { get; init; }
}

public sealed class MattermostBotConfig {
	public required string AgentName { get; init; }
	public required string Token { get; init; }
	public required string UserId { get; init; }
}
