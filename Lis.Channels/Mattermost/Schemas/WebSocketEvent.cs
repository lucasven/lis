using System.Text.Json.Serialization;

namespace Lis.Channels.Mattermost.Schemas;

public sealed record WsPost {
	[JsonPropertyName("id")]
	public string Id { get; init; } = "";

	[JsonPropertyName("channel_id")]
	public string ChannelId { get; init; } = "";

	[JsonPropertyName("user_id")]
	public string UserId { get; init; } = "";

	[JsonPropertyName("message")]
	public string Message { get; init; } = "";

	[JsonPropertyName("root_id")]
	public string? RootId { get; init; }

	[JsonPropertyName("file_ids")]
	public string[]? FileIds { get; init; }

	[JsonPropertyName("create_at")]
	public long CreateAt { get; init; }
}

public sealed record WsReaction {
	[JsonPropertyName("user_id")]
	public string UserId { get; init; } = "";

	[JsonPropertyName("post_id")]
	public string PostId { get; init; } = "";

	[JsonPropertyName("emoji_name")]
	public string EmojiName { get; init; } = "";

	[JsonPropertyName("create_at")]
	public long CreateAt { get; init; }
}
