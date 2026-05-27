# Send Files/Images to Mattermost — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable AI agents to send files and images to Mattermost channels, with caption text, through a `send_file` kernel function.

**Architecture:** New `MediaUpload` record extends `MediaDownload`. `IChannelClient` gains `SendFileAsync`. Mattermost implements via two API calls (upload file, then create post with file_ids). A `SendFilePlugin` exposes this to agents, reading files from the workspace with the same jail as `FileSystemPlugin`.

**Tech Stack:** ASP.NET Core, Semantic Kernel, Mattermost API v4, MimeTypes NuGet (source-only)

---

### Task 1: Add MimeTypes NuGet package

**Files:**
- Modify: `Lis.Core/Lis.Core.csproj`

- [ ] **Step 1: Add the MimeTypes source-only package to Lis.Core**

```xml
<PackageReference Include="MimeTypes" Version="2.5.2" PrivateAssets="all" />
```

Add this inside the existing `<ItemGroup>` with other `PackageReference` entries in `Lis.Core/Lis.Core.csproj`.

- [ ] **Step 2: Restore and verify it compiles**

Run: `dotnet restore Lis.Core/Lis.Core.csproj && dotnet build Lis.Core/Lis.Core.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Lis.Core/Lis.Core.csproj
git commit -m "📦 chore(core): add MimeTypes source-only NuGet package"
```

---

### Task 2: Create MediaUpload record

**Files:**
- Modify: `Lis.Core/Channel/MediaDownload.cs`
- Test: `Lis.Tests/Channel/MediaUploadTests.cs`

- [ ] **Step 1: Write tests for MediaUpload**

Create `Lis.Tests/Channel/MediaUploadTests.cs`:

```csharp
using Lis.Core.Channel;

namespace Lis.Tests.Channel;

public class MediaUploadTests {
	[Fact]
	public void Inherits_MediaDownload() {
		MediaUpload upload = new([1, 2, 3], "image/png");
		MediaDownload download = upload;

		Assert.Equal([1, 2, 3], download.Data);
		Assert.Equal("image/png", download.MimeType);
	}

	[Fact]
	public void Filename_Defaults_To_Null() {
		MediaUpload upload = new([1], "image/png");
		Assert.Null(upload.Filename);
	}

	[Fact]
	public void Filename_Can_Be_Set() {
		MediaUpload upload = new([1], "image/png", "screenshot.png");
		Assert.Equal("screenshot.png", upload.Filename);
	}

	[Fact]
	public void ResolveFilename_Uses_Explicit_Filename() {
		MediaUpload upload = new([1], "image/png", "my-chart.png");
		Assert.Equal("my-chart.png", upload.ResolveFilename());
	}

	[Fact]
	public void ResolveFilename_Derives_From_MimeType() {
		MediaUpload upload = new([1], "image/jpeg");
		Assert.Equal("file.jpeg", upload.ResolveFilename());
	}

	[Fact]
	public void ResolveFilename_Falls_Back_To_Bin() {
		MediaUpload upload = new([1], "application/x-unknown-type-xyz");
		Assert.Equal("file.bin", upload.ResolveFilename());
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter "FullyQualifiedName~MediaUploadTests" -v n`
Expected: FAIL — `MediaUpload` does not exist

- [ ] **Step 3: Implement MediaUpload**

Modify `Lis.Core/Channel/MediaDownload.cs`:

```csharp
namespace Lis.Core.Channel;

/// <summary>Raw media downloaded from the messaging platform.</summary>
public record MediaDownload(byte[] Data, string MimeType);

/// <summary>Media to upload to a messaging platform.</summary>
public sealed record MediaUpload(byte[] Data, string MimeType, string? Filename = null)
	: MediaDownload(Data, MimeType) {

	/// <summary>
	/// Returns the explicit filename or derives a default from MimeType.
	/// </summary>
	public string ResolveFilename() {
		if (this.Filename is { Length: > 0 }) return this.Filename;

		if (MimeTypes.MimeTypeMap.TryGetExtension(this.MimeType, out string? ext))
			return $"file{ext}";

		return "file.bin";
	}
}
```

Note: `MediaDownload` changes from `sealed record` to `record` (unseal it).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter "FullyQualifiedName~MediaUploadTests" -v n`
Expected: All 6 tests PASS

- [ ] **Step 5: Commit**

```bash
git add Lis.Core/Channel/MediaDownload.cs Lis.Tests/Channel/MediaUploadTests.cs
git commit -m "✨ feat(core): add MediaUpload record extending MediaDownload"
```

---

### Task 3: Add SendFileAsync to IChannelClient

**Files:**
- Modify: `Lis.Core/Channel/IChannelClient.cs`
- Modify: `Lis.Channels/WhatsApp/WhatsAppClient.cs`

- [ ] **Step 1: Add SendFileAsync to IChannelClient**

Add to `Lis.Core/Channel/IChannelClient.cs`:

```csharp
Task<string?> SendFileAsync(string chatId, MediaUpload media,
	string? caption = null, string? replyToId = null, CancellationToken ct = default);
```

- [ ] **Step 2: Add NotSupportedException stub in WhatsAppClient**

Add to `Lis.Channels/WhatsApp/WhatsAppClient.cs`:

```csharp
public Task<string?> SendFileAsync(string chatId, MediaUpload media,
	string? caption = null, string? replyToId = null, CancellationToken ct = default) =>
	throw new NotSupportedException("File sending is not supported on WhatsApp yet.");
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build`
Expected: Build succeeded (MattermostClient will fail — that's Task 4)

Note: If other `IChannelClient` implementations exist (Telegram, Discord stubs), add the same `NotSupportedException` stub to each.

- [ ] **Step 4: Commit**

```bash
git add Lis.Core/Channel/IChannelClient.cs Lis.Channels/WhatsApp/WhatsAppClient.cs
git commit -m "✨ feat(core): add SendFileAsync to IChannelClient interface"
```

---

### Task 4: Implement Mattermost file upload and post with file_ids

**Files:**
- Modify: `Lis.Channels/Mattermost/MattermostApiClient.cs`
- Modify: `Lis.Channels/Mattermost/MattermostClient.cs`

- [ ] **Step 1: Add UploadFileAsync to MattermostApiClient**

Add to `MattermostApiClient`:

```csharp
[Trace("MattermostApiClient > UploadFileAsync")]
public async Task<string[]> UploadFileAsync(
	string channelId, string filename, byte[] data, string contentType,
	CancellationToken ct = default) {

	using MultipartFormDataContent form = new();
	form.Add(new StringContent(channelId), "channel_id");
	form.Add(new ByteArrayContent(data) {
		Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) }
	}, "files", filename);

	HttpResponseMessage response = await http.PostAsync("/api/v4/files", form, ct);
	response.EnsureSuccessStatusCode();

	JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
	JsonArray? infos = json?["file_infos"]?.AsArray();
	if (infos is null or { Count: 0 })
		throw new InvalidOperationException("File upload returned no file_infos");

	return infos.Select(i => i!["id"]!.GetValue<string>()).ToArray();
}
```

Add `using System.Text.Json.Nodes;` to the file imports.

- [ ] **Step 2: Extend CreatePostAsync with optional fileIds**

Change `CreatePostAsync` signature and payload:

```csharp
[Trace("MattermostApiClient > CreatePostAsync")]
public async Task<MattermostPost?> CreatePostAsync(
	string channelId, string message, string? rootId = null,
	string[]? fileIds = null, CancellationToken ct = default) {

	var payload = new {
		channel_id = channelId,
		message,
		root_id = rootId ?? "",
		file_ids = fileIds ?? Array.Empty<string>()
	};

	var response = await http.PostAsJsonAsync("/api/v4/posts", payload, ct);
	response.EnsureSuccessStatusCode();
	return await response.Content.ReadFromJsonAsync<MattermostPost>(ct);
}
```

- [ ] **Step 3: Implement SendFileAsync in MattermostClient**

Add to `MattermostClient`:

```csharp
[Trace("MattermostClient > SendFileAsync")]
public async Task<string?> SendFileAsync(
	string chatId, MediaUpload media,
	string? caption = null, string? replyToId = null, CancellationToken ct = default) {

	Activity.Current?.SetTag("chat.id", chatId);
	Activity.Current?.SetTag("file.mime_type", media.MimeType);
	Activity.Current?.SetTag("file.size", media.Data.Length);

	MattermostApiClient api = this.ResolveApiClient();

	string filename = media.ResolveFilename();
	string[] fileIds = await api.UploadFileAsync(chatId, filename, media.Data, media.MimeType, ct);

	MattermostPost? post = await api.CreatePostAsync(chatId, caption ?? "", replyToId, fileIds, ct);
	return post?.Id;
}
```

Add `using Lis.Core.Channel;` if not already imported.

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add Lis.Channels/Mattermost/MattermostApiClient.cs Lis.Channels/Mattermost/MattermostClient.cs
git commit -m "✨ feat(mattermost): implement file upload and SendFileAsync"
```

---

### Task 5: Create SendFilePlugin agent tool

**Files:**
- Create: `Lis.Tools/SendFilePlugin.cs`
- Test: `Lis.Tests/Tools/SendFilePluginTests.cs`

- [ ] **Step 1: Write tests for SendFilePlugin path resolution and MIME detection**

Create `Lis.Tests/Tools/SendFilePluginTests.cs`:

```csharp
using Lis.Core.Channel;
using Lis.Tools;

using Moq;

namespace Lis.Tests.Tools;

public class SendFilePluginTests {
	[Fact]
	public void ResolveMimeType_From_Extension() {
		Assert.Equal("image/png", SendFilePlugin.ResolveMimeType("screenshot.png", null));
		Assert.Equal("image/jpeg", SendFilePlugin.ResolveMimeType("photo.jpg", null));
		Assert.Equal("application/pdf", SendFilePlugin.ResolveMimeType("doc.pdf", null));
		Assert.Equal("text/html", SendFilePlugin.ResolveMimeType("page.html", null));
		Assert.Equal("text/markdown", SendFilePlugin.ResolveMimeType("readme.md", null));
	}

	[Fact]
	public void ResolveMimeType_Override_Takes_Precedence() {
		Assert.Equal("image/webp", SendFilePlugin.ResolveMimeType("file.png", "image/webp"));
	}

	[Fact]
	public void ResolveMimeType_Unknown_Extension_Falls_Back() {
		Assert.Equal("application/octet-stream", SendFilePlugin.ResolveMimeType("file.xyz123", null));
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter "FullyQualifiedName~SendFilePluginTests" -v n`
Expected: FAIL — `SendFilePlugin` does not exist

- [ ] **Step 3: Implement SendFilePlugin**

Create `Lis.Tools/SendFilePlugin.cs`:

```csharp
using System.ComponentModel;

using Lis.Core.Channel;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace Lis.Tools;

public sealed class SendFilePlugin(IServiceScopeFactory scopeFactory) {

	[KernelFunction("send_file")]
	[Description("Send a file from the workspace to the current chat. Path is relative to workspace (e.g. 'screenshots/page.png') or absolute within the workspace directory.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> SendFileAsync(
		[Description("File path relative to workspace")] string path,
		[Description("Optional message to send with the file")] string? caption = null,
		[Description("MIME type override (auto-detected from extension if omitted)")] string? mimeType = null) {

		if (ToolContext.Channel is null || ToolContext.ChatId is null)
			return "Error: no channel context available.";

		string resolved = this.ResolveSafePath(path);
		string relativePath = Path.GetRelativePath(this.ResolveWorkspacePath(), resolved);
		await ToolContext.NotifyAsync($"📎 Sending file\n{relativePath}");

		if (!File.Exists(resolved))
			return $"Error: file not found: {relativePath}";

		byte[] data = await File.ReadAllBytesAsync(resolved);
		string resolvedMime = ResolveMimeType(resolved, mimeType);

		MediaUpload media = new(data, resolvedMime, Path.GetFileName(resolved));

		string? messageId = await ToolContext.Channel.SendFileAsync(
			ToolContext.ChatId, media, caption, ct: CancellationToken.None);

		return messageId is not null
			? $"File sent: {relativePath} ({FormatSize(data.Length)})"
			: $"File sent: {relativePath} (no message ID returned)";
	}

	internal static string ResolveMimeType(string filePath, string? mimeTypeOverride) {
		if (mimeTypeOverride is { Length: > 0 }) return mimeTypeOverride;

		if (MimeTypes.MimeTypeMap.TryGetMimeType(filePath, out string? detected))
			return detected;

		return "application/octet-stream";
	}

	private string ResolveWorkspacePath() {
		long agentId = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		AgentEntity? agent = db.Agents.Find(agentId);
		return agent?.WorkspacePath ?? Directory.GetCurrentDirectory();
	}

	private string ResolveSafePath(string userPath) {
		string workspace = this.ResolveWorkspacePath();
		string resolved = Path.GetFullPath(userPath, workspace);
		if (!resolved.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
			throw new UnauthorizedAccessException($"Path outside workspace: {userPath}");
		if (File.Exists(resolved)) {
			string? linkTarget = File.ResolveLinkTarget(resolved, true)?.FullName;
			if (linkTarget is not null && !linkTarget.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
				throw new UnauthorizedAccessException("Symlink target outside workspace");
		}
		return resolved;
	}

	private static string FormatSize(long bytes) => bytes switch {
		>= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
		>= 1_024     => $"{bytes / 1_024.0:F1} KB",
		_             => $"{bytes} B",
	};
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter "FullyQualifiedName~SendFilePluginTests" -v n`
Expected: All 3 tests PASS

- [ ] **Step 5: Commit**

```bash
git add Lis.Tools/SendFilePlugin.cs Lis.Tests/Tools/SendFilePluginTests.cs
git commit -m "✨ feat(tools): add SendFilePlugin for agent file sending"
```

---

### Task 6: Register SendFilePlugin in agent setup

**Files:**
- Modify: `Lis.Agent/AgentSetup.cs`

- [ ] **Step 1: Check current plugin registration pattern**

Read `Lis.Agent/AgentSetup.cs` to see how existing plugins (FileSystemPlugin, WebPlugin, etc.) are registered. Follow the same pattern.

- [ ] **Step 2: Register SendFilePlugin**

Add `SendFilePlugin` to the kernel plugin registration alongside the existing tools. This typically looks like:

```csharp
kernel.Plugins.AddFromObject(serviceProvider.GetRequiredService<SendFilePlugin>());
```

Or however the existing plugins are registered — follow the exact same pattern.

- [ ] **Step 3: Verify it compiles and existing tests pass**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: Build succeeded, all tests pass

- [ ] **Step 4: Commit**

```bash
git add Lis.Agent/AgentSetup.cs
git commit -m "✨ feat(agent): register SendFilePlugin in kernel"
```

---

### Task 7: Run full cleanup and final verification

- [ ] **Step 1: Run ReSharper cleanup**

```bash
jb cleanupcode Lis.Api/Lis.Api.csproj --profile="Built-in: Full Cleanup" --settings=Lis.sln.DotSettings
```

- [ ] **Step 2: Verify all tests pass**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: All tests pass (240+ existing + new tests)

- [ ] **Step 3: Review diff and commit any cleanup changes**

```bash
git diff --stat
git add -A
git commit -m "🧹 chore: code cleanup after send-files implementation"
```
