using System.Text.Json.Serialization;

namespace Lis.Channels.Mattermost.Schemas;

public sealed class OutgoingWebhookPayload {
	[JsonPropertyName("token")]
	public string Token { get; init; } = "";

	[JsonPropertyName("team_id")]
	public string TeamId { get; init; } = "";

	[JsonPropertyName("team_domain")]
	public string? TeamDomain { get; init; }

	[JsonPropertyName("channel_id")]
	public string ChannelId { get; init; } = "";

	[JsonPropertyName("channel_name")]
	public string? ChannelName { get; init; }

	[JsonPropertyName("timestamp")]
	public long Timestamp { get; init; }

	[JsonPropertyName("user_id")]
	public string UserId { get; init; } = "";

	[JsonPropertyName("user_name")]
	public string? UserName { get; init; }

	[JsonPropertyName("post_id")]
	public string PostId { get; init; } = "";

	[JsonPropertyName("text")]
	public string? Text { get; init; }

	[JsonPropertyName("trigger_word")]
	public string? TriggerWord { get; init; }

	[JsonPropertyName("file_ids")]
	public string? FileIds { get; init; }
}
