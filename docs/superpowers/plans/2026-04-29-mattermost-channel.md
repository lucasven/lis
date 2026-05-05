# Mattermost Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Prerequisite:** Complete [Multi-Channel Foundation](2026-04-29-multi-channel-foundation.md) first.

**Goal:** Add Mattermost as a messaging channel using the Mattermost REST API v4 with outgoing webhooks for inbound messages and the Bot API for outbound.

**Architecture:** Mattermost uses outgoing webhooks (HTTP POST to your server) for inbound messages and a REST API for outbound. Use a raw `HttpClient` to call the Mattermost API (no official .NET SDK needed — the API is simple REST). Register a webhook controller at `POST /webhook/mattermost`. The `MattermostClient` implements `IChannelClient` for outbound messaging.

**Tech Stack:** HttpClient for Mattermost REST API v4, ASP.NET Core webhook, keyed `IChannelClient` registration

---

### Task 1: Create MattermostOptions

**Files:**
- Create: `Lis.Channels/Mattermost/MattermostOptions.cs`

- [ ] **Step 1: Define options**

```csharp
namespace Lis.Channels.Mattermost;

public sealed class MattermostOptions {
	public required string BaseUrl { get; init; }
	public required string BotToken { get; init; }
	public string? WebhookSecret { get; init; }
	public string? BotUserId { get; init; }
}
```

- [ ] **Step 2: Commit**

```bash
git add Lis.Channels/Mattermost/MattermostOptions.cs
git commit -m "feat(mattermost): add MattermostOptions configuration

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 2: Create Mattermost Webhook Schemas

**Files:**
- Create: `Lis.Channels/Mattermost/Schemas/OutgoingWebhookPayload.cs`

- [ ] **Step 1: Define the outgoing webhook payload**

Mattermost outgoing webhooks send a form-encoded POST with these fields:

```csharp
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
```

- [ ] **Step 2: Commit**

```bash
git add Lis.Channels/Mattermost/Schemas/OutgoingWebhookPayload.cs
git commit -m "feat(mattermost): add webhook payload schema

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 3: Create MattermostApiClient

**Files:**
- Create: `Lis.Channels/Mattermost/MattermostApiClient.cs`

- [ ] **Step 1: Implement REST API wrapper**

```csharp
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
```

- [ ] **Step 2: Build and commit**

```bash
git add Lis.Channels/Mattermost/MattermostApiClient.cs
git commit -m "feat(mattermost): add REST API client

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 4: Create MattermostFormatter

**Files:**
- Create: `Lis.Channels/Mattermost/MattermostFormatter.cs`

- [ ] **Step 1: Implement formatter**

Mattermost uses standard Markdown — passthrough with length limit:

```csharp
using Lis.Core.Channel;

namespace Lis.Channels.Mattermost;

public sealed class MattermostFormatter : IMessageFormatter {
	public string Format(string content) {
		// Mattermost supports standard Markdown and has a 16383 char limit
		return content.Length > 16383 ? content[..16380] + "..." : content;
	}
}
```

- [ ] **Step 2: Commit**

```bash
git add Lis.Channels/Mattermost/MattermostFormatter.cs
git commit -m "feat(mattermost): add MattermostFormatter

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 5: Implement MattermostClient (IChannelClient)

**Files:**
- Create: `Lis.Channels/Mattermost/MattermostClient.cs`

- [ ] **Step 1: Implement the channel client**

```csharp
using Lis.Core.Channel;
using Lis.Core.Util;

namespace Lis.Channels.Mattermost;

public sealed class MattermostClient(MattermostApiClient api, MattermostFormatter formatter) : IChannelClient {

	[Trace("MattermostClient > SendMessageAsync")]
	public async Task<string?> SendMessageAsync(
		string chatId, string message, string? replyToId = null, CancellationToken ct = default) {

		string formatted = formatter.Format(message);
		MattermostPost? post = await api.CreatePostAsync(chatId, formatted, replyToId, ct);
		return post?.Id;
	}

	[Trace("MattermostClient > SetTypingAsync")]
	public Task SetTypingAsync(string chatId, CancellationToken ct = default) {
		// Mattermost typing indicators require WebSocket — skip for now
		return Task.CompletedTask;
	}

	[Trace("MattermostClient > StopTypingAsync")]
	public Task StopTypingAsync(string chatId, CancellationToken ct = default) {
		return Task.CompletedTask;
	}

	[Trace("MattermostClient > MarkReadAsync")]
	public Task MarkReadAsync(string messageId, string chatId, CancellationToken ct = default) {
		// Would need POST /api/v4/channels/{chatId}/members/me/view — skip for now
		return Task.CompletedTask;
	}

	[Trace("MattermostClient > ReactAsync")]
	public Task ReactAsync(string messageId, string chatId, string emoji, CancellationToken ct = default) {
		// Would need POST /api/v4/reactions — can be added later
		return Task.CompletedTask;
	}

	[Trace("MattermostClient > DownloadMediaAsync")]
	public async Task<MediaDownload?> DownloadMediaAsync(
		string messageId, string chatId, string? mediaPath = null, CancellationToken ct = default) {

		if (mediaPath is null) return null;

		byte[] data = await api.GetFileAsync(mediaPath, ct);
		if (data.Length == 0) return null;

		return new MediaDownload(data, "application/octet-stream");
	}
}
```

- [ ] **Step 2: Build and commit**

```bash
git add Lis.Channels/Mattermost/MattermostClient.cs
git commit -m "feat(mattermost): implement MattermostClient (IChannelClient)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 6: Create Mattermost Webhook Controller

**Files:**
- Create: `Lis.Channels/Mattermost/MattermostWebhookController.cs`

- [ ] **Step 1: Implement the controller**

```csharp
using Lis.Channels.Mattermost.Schemas;
using Lis.Core.Channel;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lis.Channels.Mattermost;

[ApiController]
[Route("webhook/mattermost")]
[Tags("Mattermost")]
public class MattermostWebhookController(
	IConversationService                    conversationService,
	IOptions<MattermostOptions>             options,
	ILogger<MattermostWebhookController>    logger) : ControllerBase {

	[HttpPost]
	[ProducesResponseType(StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status401Unauthorized)]
	public IActionResult HandleWebhook([FromBody] OutgoingWebhookPayload payload) {

		// Validate token
		if (options.Value.WebhookSecret is { Length: > 0 } secret && payload.Token != secret)
			return this.Unauthorized();

		// Skip bot's own messages
		if (options.Value.BotUserId is { Length: > 0 } botId && payload.UserId == botId)
			return this.Ok();

		if (string.IsNullOrEmpty(payload.Text))
			return this.Ok();

		// Mattermost channels starting with D or G are direct/group DMs
		bool isGroup = payload.ChannelName is not null
		            && !payload.ChannelName.StartsWith("D", StringComparison.Ordinal);

		// Check for bot mention in text (e.g. @botname)
		bool isBotMentioned = false; // Can be refined with bot username lookup

		// Extract file IDs for media
		string? mediaType = null;
		string? mediaPath = null;
		if (payload.FileIds is { Length: > 0 }) {
			mediaType = "file";
			mediaPath = payload.FileIds.Split(',')[0]; // First file ID
		}

		IncomingMessage message = new() {
			ExternalId     = payload.PostId,
			ChatId         = payload.ChannelId,
			SenderId       = payload.UserId,
			SenderName     = payload.UserName,
			Timestamp      = DateTimeOffset.FromUnixTimeSeconds(payload.Timestamp),
			IsFromMe       = false,
			IsGroup        = isGroup,
			Body           = payload.Text,
			IsBotMentioned = isBotMentioned,
			ChatName       = payload.ChannelName,
			Channel        = "mattermost"
		};

		_ = Task.Run(async () => {
			try {
				await conversationService.HandleIncomingAsync(message, CancellationToken.None);
			} catch (Exception ex) {
				logger.LogError(ex, "Error processing Mattermost message {PostId}", payload.PostId);
			}
		});

		return this.Ok();
	}
}
```

- [ ] **Step 2: Build and commit**

```bash
git add Lis.Channels/Mattermost/MattermostWebhookController.cs
git commit -m "feat(mattermost): add webhook controller

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 7: Create MattermostSetup and Wire into Program.cs

**Files:**
- Create: `Lis.Channels/Mattermost/MattermostSetup.cs`
- Modify: `Lis.Api/Program.cs`

- [ ] **Step 1: Implement setup**

```csharp
using System.Net.Http.Headers;

using Lis.Core.Channel;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lis.Channels.Mattermost;

public static class MattermostSetup {
	public static IServiceCollection AddMattermost(this IServiceCollection services) {
		MattermostOptions opts = new() {
			BaseUrl       = Env("MATTERMOST_URL"),
			BotToken      = Env("MATTERMOST_BOT_TOKEN"),
			WebhookSecret = Env("MATTERMOST_WEBHOOK_SECRET"),
			BotUserId     = Env("MATTERMOST_BOT_USER_ID"),
		};

		services.AddSingleton(Options.Create(opts));

		services.AddHttpClient<MattermostApiClient>((sp, client) => {
			client.BaseAddress = new Uri(opts.BaseUrl);
			client.DefaultRequestHeaders.Authorization =
				new AuthenticationHeaderValue("Bearer", opts.BotToken);
		});

		services.AddSingleton<MattermostFormatter>();
		services.AddKeyedScoped<IChannelClient, MattermostClient>("mattermost");

		return services;
	}

	private static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? "";
}
```

- [ ] **Step 2: Wire into Program.cs**

Add `using Lis.Channels.Mattermost;` and:
```csharp
if (Env("MATTERMOST_ENABLED") == "true") builder.Services.AddMattermost();
```

- [ ] **Step 3: Build, test, and commit**

```bash
git add Lis.Channels/Mattermost/MattermostSetup.cs Lis.Api/Program.cs
git commit -m "feat(mattermost): complete Mattermost channel integration

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 8: End-to-End Verification and Cleanup

- [ ] **Step 1: Build and test**
- [ ] **Step 2: Code cleanup with jb cleanupcode**
- [ ] **Step 3: Final commit**
