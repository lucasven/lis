using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Lis.Core.Util;

namespace Lis.Channels.Mattermost;

public sealed class MattermostApiClient(HttpClient http) {

	[Trace("MattermostApiClient > CreatePostAsync")]
	public async Task<MattermostPost?> CreatePostAsync(
		string channelId, string message, string? rootId = null, CancellationToken ct = default) {

		var payload = new {
			channel_id = channelId,
			message,
			root_id = rootId ?? ""
		};

		var response = await http.PostAsJsonAsync("/api/v4/posts", payload, ct);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadFromJsonAsync<MattermostPost>(ct);
	}

	[Trace("MattermostApiClient > GetPostAsync")]
	public async Task<MattermostPost?> GetPostAsync(string postId, CancellationToken ct = default) {
		return await http.GetFromJsonAsync<MattermostPost>($"/api/v4/posts/{postId}", ct);
	}

	[Trace("MattermostApiClient > GetChannelAsync")]
	public async Task<MattermostChannel?> GetChannelAsync(string channelId, CancellationToken ct = default) {
		return await http.GetFromJsonAsync<MattermostChannel>($"/api/v4/channels/{channelId}", ct);
	}

	[Trace("MattermostApiClient > GetFileAsync")]
	public async Task<byte[]> GetFileAsync(string fileId, CancellationToken ct = default) {
		return await http.GetByteArrayAsync($"/api/v4/files/{fileId}", ct);
	}

	[Trace("MattermostApiClient > CreateReactionAsync")]
	public async Task CreateReactionAsync(
		string userId, string postId, string emojiName, CancellationToken ct = default) {

		var payload = new {
			user_id    = userId,
			post_id    = postId,
			emoji_name = emojiName,
			create_at  = 0
		};

		HttpResponseMessage response = await http.PostAsJsonAsync("/api/v4/reactions", payload, ct);
		response.EnsureSuccessStatusCode();
	}

	[Trace("MattermostApiClient > DeleteReactionAsync")]
	public async Task DeleteReactionAsync(
		string userId, string postId, string emojiName, CancellationToken ct = default) {

		HttpResponseMessage response = await http.DeleteAsync(
			$"/api/v4/reactions/{userId}/{postId}/{emojiName}", ct);
		response.EnsureSuccessStatusCode();
	}

	[Trace("MattermostApiClient > ViewChannelAsync")]
	public async Task ViewChannelAsync(string userId, string channelId, CancellationToken ct = default) {
		HttpResponseMessage response = await http.PostAsJsonAsync(
			$"/api/v4/channels/members/{userId}/view",
			new { channel_id = channelId, collapsed_threads_supported = true }, ct);
		response.EnsureSuccessStatusCode();
	}
}

public sealed class MattermostPost {
	[JsonPropertyName("id")]
	public string Id { get; init; } = "";

	[JsonPropertyName("channel_id")]
	public string ChannelId { get; init; } = "";

	[JsonPropertyName("message")]
	public string? Message { get; init; }

	[JsonPropertyName("root_id")]
	public string? RootId { get; init; }
}

public sealed class MattermostChannel {
	[JsonPropertyName("id")]
	public string Id { get; init; } = "";

	[JsonPropertyName("display_name")]
	public string? DisplayName { get; init; }

	[JsonPropertyName("type")]
	public string Type { get; init; } = "";

	[JsonPropertyName("header")]
	public string? Header { get; init; }

	[JsonPropertyName("purpose")]
	public string? Purpose { get; init; }
}
