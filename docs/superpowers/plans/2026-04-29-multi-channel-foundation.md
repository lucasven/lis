# Multi-Channel Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the channel architecture so multiple `IChannelClient` implementations (WhatsApp, Telegram, Discord, Mattermost) can coexist and the system routes messages through the correct channel.

**Architecture:** Add a `Channel` property to `IncomingMessage` to identify the source channel. Replace the single `IChannelClient` registration with keyed services (`AddKeyedScoped`) so each channel registers under its own name. Introduce `IChannelClientProvider` to resolve the correct client at runtime. Each webhook controller sets the channel name on the incoming message, and `ConversationService`/`MessageDebouncer` use it to resolve the right client.

**Tech Stack:** .NET 10 keyed services, ASP.NET Core DI

---

### Task 1: Add Channel Identifier to IncomingMessage

**Files:**
- Modify: `Lis.Core/Channel/IncomingMessage.cs:3-23`

- [ ] **Step 1: Add Channel property to IncomingMessage**

```csharp
public sealed class IncomingMessage {
	public required string ExternalId { get; init; }
	public required string ChatId { get; init; }
	public required string SenderId { get; init; }
	public string? SenderName { get; init; }
	public DateTimeOffset Timestamp { get; init; }
	public bool IsFromMe { get; init; }
	public bool IsGroup { get; init; }
	public string? Body { get; init; }
	public string? RepliedId { get; init; }
	public string? RepliedContent { get; init; }
	public bool IsBotMentioned { get; set; }
	public string? ChatName { get; set; }
	public string? ChatTopic { get; set; }
	public string? MediaType { get; init; }
	public string? MediaCaption { get; init; }
	public string? MediaPath { get; init; }

	/// <summary>Channel this message came from (e.g. "whatsapp", "telegram", "discord", "mattermost").</summary>
	public required string Channel { get; init; }

	/// <summary>Set after ingestion -- the DB-generated primary key.</summary>
	public long DbId { get; set; }
}
```

- [ ] **Step 2: Build to verify compilation fails where IncomingMessage is constructed without Channel**

Run: `dotnet build`
Expected: Compilation errors in `GowaWebhookController.cs` (and tests if any construct `IncomingMessage`)

- [ ] **Step 3: Fix GowaWebhookController to set Channel = "whatsapp"**

In `Lis.Channels/WhatsApp/GowaWebhookController.cs`, add `Channel = "whatsapp"` to the `IncomingMessage` initializer (around line 90):

```csharp
IncomingMessage message = new() {
	ExternalId     = payload.Id,
	ChatId         = payload.ChatId ?? "",
	SenderId       = payload.From   ?? "",
	SenderName     = payload.FromName,
	Timestamp      = timestamp,
	IsFromMe       = payload.IsFromMe,
	IsGroup        = isGroup,
	Body           = normalizedBody,
	RepliedId      = payload.RepliedToId,
	RepliedContent = payload.QuotedBody,
	MediaType      = payload.MediaType,
	MediaCaption   = payload.MediaCaption,
	MediaPath      = payload.MediaPath,
	Channel        = "whatsapp"
};
```

- [ ] **Step 4: Fix any test files that construct IncomingMessage**

Search for `new IncomingMessage` across the codebase and add `Channel = "whatsapp"` (or appropriate value) to each.

- [ ] **Step 5: Build to verify everything compiles**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 6: Run tests**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: All pass

- [ ] **Step 7: Commit**

```bash
git add Lis.Core/Channel/IncomingMessage.cs Lis.Channels/WhatsApp/GowaWebhookController.cs
# Also add any test files modified
git commit -m "feat(core): add Channel identifier to IncomingMessage

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 2: Create IChannelClientProvider

**Files:**
- Create: `Lis.Core/Channel/IChannelClientProvider.cs`

- [ ] **Step 1: Define the provider interface**

```csharp
namespace Lis.Core.Channel;

public interface IChannelClientProvider {
	IChannelClient Get(string channel);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS (interface only, no consumers yet)

- [ ] **Step 3: Commit**

```bash
git add Lis.Core/Channel/IChannelClientProvider.cs
git commit -m "feat(core): add IChannelClientProvider interface

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 3: Implement ChannelClientProvider Using Keyed Services

**Files:**
- Create: `Lis.Core/Channel/ChannelClientProvider.cs`

- [ ] **Step 1: Implement the provider**

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Lis.Core.Channel;

public sealed class ChannelClientProvider(IServiceProvider serviceProvider) : IChannelClientProvider {
	public IChannelClient Get(string channel) {
		return serviceProvider.GetRequiredKeyedService<IChannelClient>(channel);
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Core/Channel/ChannelClientProvider.cs
git commit -m "feat(core): implement ChannelClientProvider with keyed services

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 4: Switch WhatsApp to Keyed Registration

**Files:**
- Modify: `Lis.Channels/WhatsApp/WhatsAppSetup.cs:38`
- Modify: `Lis.Api/Program.cs:108`

- [ ] **Step 1: Change WhatsApp registration to keyed scoped service**

In `Lis.Channels/WhatsApp/WhatsAppSetup.cs`, change line 38 from:

```csharp
services.AddScoped<IChannelClient, WhatsAppClient>();
```

to:

```csharp
services.AddKeyedScoped<IChannelClient, WhatsAppClient>("whatsapp");
```

- [ ] **Step 2: Register ChannelClientProvider and backward-compat IChannelClient in Program.cs**

In `Lis.Api/Program.cs`, after the channel registration block (around line 108), add the provider registration and a default `IChannelClient` that resolves to whatsapp (backward compatibility during migration):

```csharp
// Channel
if (Env("GOWA_ENABLED") == "true") builder.Services.AddWhatsApp();

// Channel provider (resolves keyed IChannelClient by name)
builder.Services.AddScoped<IChannelClientProvider, ChannelClientProvider>();

// Default IChannelClient — backward compat (resolves to whatsapp)
builder.Services.AddScoped<IChannelClient>(sp => sp.GetRequiredKeyedService<IChannelClient>("whatsapp"));
```

Note: The backward-compat registration lets `ConversationService` and other consumers keep working with plain `IChannelClient` injection until we migrate them in the next task.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 4: Run tests**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add Lis.Channels/WhatsApp/WhatsAppSetup.cs Lis.Api/Program.cs
git commit -m "refactor(channels): switch WhatsApp to keyed service registration

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 5: Migrate ConversationService to Use IChannelClientProvider

**Files:**
- Modify: `Lis.Agent/ConversationService.cs:21-36`
- Modify: `Lis.Agent/ConversationService.cs` (all places where `channelClient` is used)

- [ ] **Step 1: Read the full ConversationService to identify all channelClient usages**

Read `Lis.Agent/ConversationService.cs` fully to locate every place `channelClient` is referenced.

- [ ] **Step 2: Replace IChannelClient with IChannelClientProvider in constructor**

Change the constructor parameter from `IChannelClient channelClient` to `IChannelClientProvider channelProvider`. Then in every method that uses `channelClient`, resolve the correct client from the message's `Channel` property:

In the constructor parameters, replace:
```csharp
IChannelClient               channelClient,
```
with:
```csharp
IChannelClientProvider       channelProvider,
```

- [ ] **Step 3: Update all channelClient usages to resolve via provider**

Wherever the service calls `channelClient.SendMessageAsync(...)`, `channelClient.SetTypingAsync(...)`, etc., it should now resolve the client from the message context. The pattern is:

```csharp
IChannelClient channel = channelProvider.Get(message.Channel);
```

For methods that don't have the message in scope but have the chat ID (like typing), you'll need to pass or store the channel name. Read the full file to determine the best approach — likely `ToolContext.ChannelName` (see next task).

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: SUCCESS (or errors that point to remaining usages)

- [ ] **Step 5: Run tests**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add Lis.Agent/ConversationService.cs
git commit -m "refactor(agent): use IChannelClientProvider in ConversationService

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 6: Add ChannelName to ToolContext

**Files:**
- Modify: `Lis.Core/Util/ToolContext.cs`

- [ ] **Step 1: Add ChannelName AsyncLocal**

Add a new `AsyncLocal<string?>` for the channel name so tools and services can resolve the correct channel client during processing:

```csharp
private static readonly AsyncLocal<string?> ChannelNameLocal = new();

public static string? ChannelName { get => ChannelNameLocal.Value; set => ChannelNameLocal.Value = value; }
```

- [ ] **Step 2: Verify ToolContext.Channel is still set correctly**

`ToolContext.Channel` (the `IChannelClient` instance) should now be set by resolving via `channelProvider.Get(ToolContext.ChannelName)` wherever it's currently set. Search for `ToolContext.Channel =` to find these locations and update them.

- [ ] **Step 3: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 4: Commit**

```bash
git add Lis.Core/Util/ToolContext.cs
# Add any files where ToolContext.Channel assignment was updated
git commit -m "feat(core): add ChannelName to ToolContext for multi-channel resolution

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 7: Migrate MessageDebouncer to Use IChannelClientProvider

**Files:**
- Modify: `Lis.Agent/MessageDebouncer.cs`

- [ ] **Step 1: Read MessageDebouncer fully**

Read the full file to understand how `IChannelClient` is resolved and used.

- [ ] **Step 2: Replace GetChannelClient with provider-based resolution**

The current pattern:
```csharp
private static IChannelClient GetChannelClient(IServiceScope scope) =>
	scope.ServiceProvider.GetRequiredService<IChannelClient>();
```

Change to accept the channel name:
```csharp
private static IChannelClient GetChannelClient(IServiceScope scope, string channel) =>
	scope.ServiceProvider.GetRequiredKeyedService<IChannelClient>(channel);
```

Update all callers to pass `message.Channel` through to this method.

- [ ] **Step 3: Store channel name in ChatState if needed**

If the debouncer needs the channel name when draining queued messages (where the original message may not be in scope), store `ChannelName` in the `ChatState` class.

- [ ] **Step 4: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 5: Commit**

```bash
git add Lis.Agent/MessageDebouncer.cs
git commit -m "refactor(agent): use keyed IChannelClient in MessageDebouncer

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 8: Migrate Remaining Consumers

**Files:**
- Modify: `Lis.Agent/MediaProcessor.cs`
- Modify: `Lis.Agent/CompactionService.cs`
- Any other files that inject `IChannelClient` directly

- [ ] **Step 1: Search for all remaining IChannelClient injections**

Run: `grep -rn "IChannelClient" --include="*.cs" Lis.Agent/ Lis.Tools/`

For each file that injects `IChannelClient` directly (not via `IChannelClientProvider`), update to use the provider or resolve from `ToolContext.ChannelName`.

- [ ] **Step 2: Update MediaProcessor**

`MediaProcessor` injects `IChannelClient` — change to `IChannelClientProvider` and resolve via the message's channel.

- [ ] **Step 3: Update CompactionService**

`CompactionService` resolves `IChannelClient` from scope — change to keyed resolution using stored channel name.

- [ ] **Step 4: Remove backward-compat IChannelClient registration from Program.cs**

Remove the line:
```csharp
builder.Services.AddScoped<IChannelClient>(sp => sp.GetRequiredKeyedService<IChannelClient>("whatsapp"));
```

- [ ] **Step 5: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 6: Commit**

```bash
git add Lis.Agent/MediaProcessor.cs Lis.Agent/CompactionService.cs Lis.Api/Program.cs
git commit -m "refactor(agent): complete migration to IChannelClientProvider

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 9: Update Conversation+Agent Guard for Multi-Channel

**Files:**
- Modify: `Lis.Api/Program.cs:115-120`

- [ ] **Step 1: Decouple ConversationService from WhatsApp-only guard**

Currently the guard is:
```csharp
if (Env("ANTHROPIC_ENABLED") == "true" && Env("GOWA_ENABLED") == "true") {
	builder.Services.AddScoped<ConversationService>();
	builder.Services.AddSingleton<IConversationService, MessageDebouncer>();
	builder.Services.AddLisAgent();
}
```

This requires WhatsApp specifically. Change to require any channel:

```csharp
bool hasChannel = Env("GOWA_ENABLED") == "true"
               || Env("TELEGRAM_ENABLED") == "true"
               || Env("DISCORD_ENABLED") == "true"
               || Env("MATTERMOST_ENABLED") == "true";

if (Env("ANTHROPIC_ENABLED") == "true" && hasChannel) {
	builder.Services.AddScoped<ConversationService>();
	builder.Services.AddSingleton<IConversationService, MessageDebouncer>();
	builder.Services.AddLisAgent();
}
```

- [ ] **Step 2: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Api/Program.cs
git commit -m "refactor(api): decouple conversation guard from WhatsApp-only check

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 10: Store Channel on ChatEntity

**Files:**
- Modify: `Lis.Persistence/Entities/ChatEntity.cs`
- Create: migration

- [ ] **Step 1: Read ChatEntity to understand current schema**

Read `Lis.Persistence/Entities/ChatEntity.cs`.

- [ ] **Step 2: Add Channel column to ChatEntity**

Add a `Channel` string column so the system knows which channel a chat belongs to:

```csharp
[Column("channel")]
[MaxLength(32)]
public string? Channel { get; set; }
```

- [ ] **Step 3: Create migration**

Run:
```bash
dotnet ef migrations add add_chat_channel --project Lis.Persistence/Lis.Persistence.csproj --startup-project Lis.Api/Lis.Api.csproj
```

- [ ] **Step 4: Set channel during chat upsert in ConversationService**

In `ConversationService.IngestMessageAsync`, when creating or updating the chat entity, set `chat.Channel = message.Channel`.

- [ ] **Step 5: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 6: Commit**

```bash
git add Lis.Persistence/Entities/ChatEntity.cs Lis.Persistence/Migrations/ Lis.Agent/ConversationService.cs
git commit -m "feat(persistence): add channel column to chat entity

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 11: Add .env.example Entries

**Files:**
- Modify: `.env.example`

- [ ] **Step 1: Add placeholder entries for future channels**

```env
# Telegram (optional)
TELEGRAM_ENABLED=false
TELEGRAM_BOT_TOKEN=
TELEGRAM_WEBHOOK_SECRET=

# Discord (optional)
DISCORD_ENABLED=false
DISCORD_BOT_TOKEN=
DISCORD_APPLICATION_ID=

# Mattermost (optional)
MATTERMOST_ENABLED=false
MATTERMOST_URL=
MATTERMOST_BOT_TOKEN=
MATTERMOST_WEBHOOK_SECRET=
```

- [ ] **Step 2: Commit**

```bash
git add .env.example
git commit -m "docs: add channel env var placeholders to .env.example

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 12: End-to-End Verification

- [ ] **Step 1: Build the full solution**

Run: `dotnet build`
Expected: SUCCESS with no warnings related to channel changes

- [ ] **Step 2: Run all tests**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: All pass

- [ ] **Step 3: Verify WhatsApp still works by reviewing the registration chain**

Trace through: `Program.cs` → `AddWhatsApp()` → keyed registration → `ChannelClientProvider.Get("whatsapp")` → `WhatsAppClient`. Confirm the flow is correct by reading the code.

- [ ] **Step 4: Run the application locally**

Run: `cd Lis.Api && dotnet run`
Expected: Application starts without errors. Migrations apply. Default agent seeds.

- [ ] **Step 5: Commit final state if any fixes were needed**

```bash
git add -A
git commit -m "fix: address multi-channel foundation review issues

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```
