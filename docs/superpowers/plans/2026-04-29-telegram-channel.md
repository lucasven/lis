# Telegram Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Prerequisite:** Complete [Multi-Channel Foundation](2026-04-29-multi-channel-foundation.md) first.

**Goal:** Add Telegram as a messaging channel, following the same pattern as WhatsApp — webhook controller, `IChannelClient` implementation, message formatter, and schema DTOs.

**Architecture:** Use the `Telegram.Bot` NuGet package (v22+) for API interactions. Register a webhook controller at `POST /webhook/telegram` that receives Telegram `Update` objects, maps them to `IncomingMessage`, and delegates to `IConversationService`. The `TelegramClient` implements `IChannelClient` and sends messages via the Bot API. A `TelegramFormatter` converts Markdown to Telegram MarkdownV2 format.

**Tech Stack:** Telegram.Bot NuGet, ASP.NET Core webhook, keyed `IChannelClient` registration

---

### Task 1: Add Telegram.Bot NuGet Package

**Files:**
- Modify: `Lis.Channels/Lis.Channels.csproj`

- [ ] **Step 1: Add the package reference**

```xml
<PackageReference Include="Telegram.Bot" Version="22.*" />
```

- [ ] **Step 2: Restore packages**

Run: `dotnet restore`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Channels/Lis.Channels.csproj
git commit -m "chore(channels): add Telegram.Bot NuGet package

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 2: Create TelegramOptions

**Files:**
- Create: `Lis.Channels/Telegram/TelegramOptions.cs`

- [ ] **Step 1: Define the options class**

```csharp
namespace Lis.Channels.Telegram;

public sealed class TelegramOptions {
	public required string BotToken { get; init; }
	public string? WebhookSecret { get; init; }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Channels/Telegram/TelegramOptions.cs
git commit -m "feat(telegram): add TelegramOptions configuration

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 3: Create Telegram Webhook Schemas

**Files:**
- Create: `Lis.Channels/Telegram/Schemas/` (use Telegram.Bot built-in types where possible)

- [ ] **Step 1: Document which Telegram.Bot types map to IncomingMessage**

The `Telegram.Bot` library provides `Update`, `Message`, `Chat`, `User` types. No custom schemas needed for inbound — use the library types directly.

For outbound, the library's `TelegramBotClient` handles serialization.

- [ ] **Step 2: Commit (no files if using library types)**

Skip if no custom schemas are needed.

---

### Task 4: Create TelegramFormatter

**Files:**
- Create: `Lis.Channels/Telegram/TelegramFormatter.cs`

- [ ] **Step 1: Write the failing test**

Create test in `Lis.Tests/Channels/Telegram/TelegramFormatterTests.cs`:

```csharp
using Lis.Channels.Telegram;

namespace Lis.Tests.Channels.Telegram;

public class TelegramFormatterTests {
	private readonly TelegramFormatter _formatter = new();

	[Fact]
	public void Format_Bold_ConvertsToMarkdownV2() {
		string result = _formatter.Format("**bold text**");
		Assert.Equal("*bold text*", result);
	}

	[Fact]
	public void Format_PlainText_EscapesSpecialChars() {
		string result = _formatter.Format("hello.world");
		Assert.Equal("hello\\.world", result);
	}

	[Fact]
	public void Format_CodeBlock_PreservesContent() {
		string result = _formatter.Format("```csharp\nvar x = 1;\n```");
		Assert.Equal("```csharp\nvar x = 1;\n```", result);
	}
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter "TelegramFormatterTests"`
Expected: FAIL — class not found

- [ ] **Step 3: Implement TelegramFormatter**

```csharp
using Lis.Core.Channel;

namespace Lis.Channels.Telegram;

public sealed class TelegramFormatter : IMessageFormatter {
	// Telegram MarkdownV2 special characters that need escaping
	private static readonly char[] SpecialChars = ['_', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!'];

	public string Format(string content) {
		// Split by code blocks to preserve their content
		var parts = content.Split(new[] { "```" }, StringSplitOptions.None);
		for (int i = 0; i < parts.Length; i++) {
			if (i % 2 == 0) // Not inside code block
				parts[i] = FormatSegment(parts[i]);
		}

		return string.Join("```", parts);
	}

	private static string FormatSegment(string text) {
		// Convert **bold** to *bold* (Telegram MarkdownV2)
		text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "*$1*");

		// Escape special characters outside of formatting
		// This is simplified — a production implementation should handle nested formatting
		foreach (char c in SpecialChars)
			text = text.Replace(c.ToString(), $"\\{c}");

		return text;
	}
}
```

Note: The formatter will need refinement as you test with real Telegram messages. Start simple and iterate.

- [ ] **Step 4: Run tests**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter "TelegramFormatterTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Lis.Channels/Telegram/TelegramFormatter.cs Lis.Tests/Channels/Telegram/TelegramFormatterTests.cs
git commit -m "feat(telegram): add TelegramFormatter for MarkdownV2

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 5: Implement TelegramClient (IChannelClient)

**Files:**
- Create: `Lis.Channels/Telegram/TelegramClient.cs`

- [ ] **Step 1: Implement TelegramClient**

```csharp
using Lis.Core.Channel;
using Lis.Core.Util;

using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Lis.Channels.Telegram;

public sealed class TelegramClient(TelegramBotClient bot, IMessageFormatter formatter) : IChannelClient {

	[Trace("TelegramClient > SendMessageAsync")]
	public async Task<string?> SendMessageAsync(
		string chatId, string message, string? replyToId = null, CancellationToken ct = default) {

		string formatted = formatter.Format(message);
		var sent = await bot.SendMessage(
			chatId: long.Parse(chatId),
			text: formatted,
			parseMode: ParseMode.MarkdownV2,
			replyParameters: replyToId is not null
				? new Telegram.Bot.Types.ReplyParameters { MessageId = int.Parse(replyToId) }
				: null,
			cancellationToken: ct);
		return sent.MessageId.ToString();
	}

	[Trace("TelegramClient > SetTypingAsync")]
	public async Task SetTypingAsync(string chatId, CancellationToken ct = default) {
		await bot.SendChatAction(long.Parse(chatId), ChatAction.Typing, cancellationToken: ct);
	}

	[Trace("TelegramClient > StopTypingAsync")]
	public Task StopTypingAsync(string chatId, CancellationToken ct = default) {
		// Telegram doesn't have explicit stop-typing — it auto-clears after ~5s or when a message is sent
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
			reaction: [new Telegram.Bot.Types.ReactionTypeEmoji { Emoji = emoji }],
			cancellationToken: ct);
	}

	[Trace("TelegramClient > DownloadMediaAsync")]
	public async Task<MediaDownload?> DownloadMediaAsync(
		string messageId, string chatId, string? mediaPath = null, CancellationToken ct = default) {

		if (mediaPath is null) return null;

		var file = await bot.GetFile(mediaPath, ct);
		if (file.FilePath is null) return null;

		using MemoryStream ms = new();
		await bot.DownloadFile(file.FilePath, ms, ct);
		byte[] data = ms.ToArray();

		string mimeType = GuessMimeType(file.FilePath);
		return new MediaDownload(data, mimeType);
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
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Channels/Telegram/TelegramClient.cs
git commit -m "feat(telegram): implement TelegramClient (IChannelClient)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 6: Create Telegram Webhook Controller

**Files:**
- Create: `Lis.Channels/Telegram/TelegramWebhookController.cs`

- [ ] **Step 1: Implement the controller**

```csharp
using Lis.Core.Channel;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Lis.Channels.Telegram;

[ApiController]
[Route("webhook/telegram")]
[Tags("Telegram")]
public class TelegramWebhookController(
	IConversationService                  conversationService,
	IOptions<TelegramOptions>             options,
	ILogger<TelegramWebhookController>    logger) : ControllerBase {

	[HttpPost]
	[ProducesResponseType(StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status401Unauthorized)]
	public async Task<IActionResult> HandleWebhook(
		[FromHeader(Name = "X-Telegram-Bot-Api-Secret-Token")] string? secretToken,
		[FromBody] Update update) {

		// Validate webhook secret
		if (options.Value.WebhookSecret is { Length: > 0 } secret && secretToken != secret)
			return this.Unauthorized();

		if (update.Type != UpdateType.Message || update.Message is null)
			return this.Ok();

		Message msg = update.Message;
		if (msg.Text is null && msg.Photo is null && msg.Voice is null && msg.Document is null)
			return this.Ok();

		bool isGroup = msg.Chat.Type is ChatType.Group or ChatType.Supergroup;
		bool isBotMentioned = false;

		// Check for bot mention in entities
		if (msg.Entities is not null) {
			foreach (var entity in msg.Entities) {
				if (entity.Type == MessageEntityType.Mention && msg.Text is not null) {
					// Could check if the mention matches the bot's username
					isBotMentioned = true;
				}
			}
		}

		// Determine media info
		string? mediaType = null;
		string? mediaPath = null;
		if (msg.Photo is { Length: > 0 }) {
			mediaType = "image";
			mediaPath = msg.Photo[^1].FileId; // Largest photo
		} else if (msg.Voice is not null) {
			mediaType = "audio";
			mediaPath = msg.Voice.FileId;
		} else if (msg.Document is not null) {
			mediaType = "document";
			mediaPath = msg.Document.FileId;
		}

		IncomingMessage message = new() {
			ExternalId     = msg.MessageId.ToString(),
			ChatId         = msg.Chat.Id.ToString(),
			SenderId       = msg.From?.Id.ToString() ?? "",
			SenderName     = BuildSenderName(msg.From),
			Timestamp      = msg.Date,
			IsFromMe       = false, // Webhooks don't receive bot's own messages
			IsGroup        = isGroup,
			Body           = msg.Text ?? msg.Caption,
			RepliedId      = msg.ReplyToMessage?.MessageId.ToString(),
			RepliedContent = msg.ReplyToMessage?.Text,
			IsBotMentioned = isBotMentioned,
			ChatName       = isGroup ? msg.Chat.Title : null,
			MediaType      = mediaType,
			MediaPath      = mediaPath,
			Channel        = "telegram"
		};

		_ = Task.Run(async () => {
			try {
				await conversationService.HandleIncomingAsync(message, CancellationToken.None);
			} catch (Exception ex) {
				logger.LogError(ex, "Error processing Telegram message {MessageId}", msg.MessageId);
			}
		});

		return this.Ok();
	}

	private static string? BuildSenderName(User? user) {
		if (user is null) return null;
		string name = user.FirstName;
		if (user.LastName is { Length: > 0 }) name += $" {user.LastName}";
		return name;
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Channels/Telegram/TelegramWebhookController.cs
git commit -m "feat(telegram): add webhook controller

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 7: Create TelegramSetup Registration

**Files:**
- Create: `Lis.Channels/Telegram/TelegramSetup.cs`

- [ ] **Step 1: Implement setup following WhatsApp pattern**

```csharp
using Lis.Core.Channel;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Telegram.Bot;

namespace Lis.Channels.Telegram;

public static class TelegramSetup {
	public static IServiceCollection AddTelegram(this IServiceCollection services) {
		TelegramOptions opts = new() {
			BotToken      = Env("TELEGRAM_BOT_TOKEN"),
			WebhookSecret = Env("TELEGRAM_WEBHOOK_SECRET"),
		};

		services.AddSingleton(Options.Create(opts));

		// Telegram.Bot client
		services.AddSingleton(new TelegramBotClient(opts.BotToken));

		// Formatter and channel client (keyed)
		services.AddSingleton<IMessageFormatter, TelegramFormatter>();
		services.AddKeyedScoped<IChannelClient, TelegramClient>("telegram");

		return services;
	}

	private static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? "";
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Channels/Telegram/TelegramSetup.cs
git commit -m "feat(telegram): add DI registration

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 8: Wire Telegram into Program.cs

**Files:**
- Modify: `Lis.Api/Program.cs`

- [ ] **Step 1: Add Telegram using and registration**

Add `using Lis.Channels.Telegram;` to the top.

After the WhatsApp registration:
```csharp
if (Env("TELEGRAM_ENABLED") == "true") builder.Services.AddTelegram();
```

Add `TelegramWebhookController` assembly to the controller registration:
```csharp
.AddApplicationPart(typeof(TelegramWebhookController).Assembly)
```

Note: Since both WhatsApp and Telegram controllers are in `Lis.Channels`, the existing `.AddApplicationPart(typeof(GowaWebhookController).Assembly)` already covers this — they're in the same assembly. No change needed for application parts.

- [ ] **Step 2: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Api/Program.cs
git commit -m "feat(telegram): wire Telegram channel into Program.cs

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 9: Webhook Registration on Startup

**Files:**
- Modify: `Lis.Api/Program.cs` (startup section)

- [ ] **Step 1: Register the webhook URL with Telegram on app start**

After `app.MapControllers()`, add:

```csharp
// Register Telegram webhook
if (Env("TELEGRAM_ENABLED") == "true" && Env("TELEGRAM_WEBHOOK_URL") is { Length: > 0 } webhookUrl) {
	TelegramBotClient telegramBot = app.Services.GetRequiredService<TelegramBotClient>();
	IOptions<TelegramOptions> telegramOpts = app.Services.GetRequiredService<IOptions<TelegramOptions>>();
	await telegramBot.SetWebhook(
		url: webhookUrl,
		secretToken: telegramOpts.Value.WebhookSecret,
		allowedUpdates: [Telegram.Bot.Types.Enums.UpdateType.Message]);
}
```

Add `TELEGRAM_WEBHOOK_URL` to `.env.example`.

- [ ] **Step 2: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Api/Program.cs .env.example
git commit -m "feat(telegram): register webhook URL on startup

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 10: IMessageFormatter Multi-Channel Conflict Resolution

**Files:**
- Modify: `Lis.Channels/WhatsApp/WhatsAppSetup.cs`
- Modify: `Lis.Channels/Telegram/TelegramSetup.cs`
- Modify: `Lis.Channels/Telegram/TelegramClient.cs`
- Modify: `Lis.Channels/WhatsApp/WhatsAppClient.cs`

- [ ] **Step 1: Identify the IMessageFormatter conflict**

Both WhatsApp and Telegram register `IMessageFormatter` as singleton. When both are enabled, the last registration wins. Fix by making each channel's client resolve its own formatter explicitly rather than via DI.

Option A: Use keyed `IMessageFormatter` services.
Option B: Inject the formatter directly into the client constructor (not via interface).

Choose Option B (simpler): Each channel client takes its specific formatter type directly.

- [ ] **Step 2: Change WhatsAppClient to inject WhatsAppFormatter directly**

```csharp
public sealed class WhatsAppClient(GowaClient gowa, WhatsAppFormatter formatter) : IChannelClient {
```

Update `WhatsAppSetup.cs`:
```csharp
services.AddSingleton<WhatsAppFormatter>();
// Remove: services.AddSingleton<IMessageFormatter, WhatsAppFormatter>();
```

- [ ] **Step 3: Change TelegramClient to inject TelegramFormatter directly**

```csharp
public sealed class TelegramClient(TelegramBotClient bot, TelegramFormatter formatter) : IChannelClient {
```

Update `TelegramSetup.cs`:
```csharp
services.AddSingleton<TelegramFormatter>();
// Remove: services.AddSingleton<IMessageFormatter, TelegramFormatter>();
```

- [ ] **Step 4: Check if IMessageFormatter is used anywhere else**

Search for `IMessageFormatter` usage. If it's only in channel clients, the interface can stay but doesn't need to be registered in DI.

- [ ] **Step 5: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 6: Commit**

```bash
git add Lis.Channels/WhatsApp/WhatsAppSetup.cs Lis.Channels/WhatsApp/WhatsAppClient.cs \
       Lis.Channels/Telegram/TelegramSetup.cs Lis.Channels/Telegram/TelegramClient.cs
git commit -m "refactor(channels): resolve IMessageFormatter per-channel to avoid conflicts

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 11: End-to-End Verification

- [ ] **Step 1: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 2: Run locally with TELEGRAM_ENABLED=false**

Run: `cd Lis.Api && dotnet run`
Expected: Starts normally, WhatsApp works as before

- [ ] **Step 3: Code cleanup**

Run: `jb cleanupcode Lis.Api/Lis.Api.csproj --profile="Built-in: Full Cleanup" --settings=Lis.sln.DotSettings`
Review changes, fix any collateral damage.

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "feat(telegram): complete Telegram channel integration

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```
