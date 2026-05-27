# Send Files/Images to Mattermost

**Date:** 2026-05-27
**Status:** Approved

## Summary

Add file/image sending support to the Mattermost channel. Expose this to AI agents via a `send_file` kernel function that reads files from the agent's workspace.

## Use Cases

- AI tools (browser screenshots, generated charts) sending output as images
- Media forwarding: image received from user, agent forwards it

## Design

### 1. MediaUpload — new record extending MediaDownload

`MediaDownload` stays unchanged (used for receiving). New `MediaUpload` extends it with an optional `Filename` for sending:

```csharp
// Existing — unsealed to allow inheritance
public record MediaDownload(byte[] Data, string MimeType);

// New — for outbound files
public sealed record MediaUpload(byte[] Data, string MimeType, string? Filename = null)
    : MediaDownload(Data, MimeType);
```

When `Filename` is null, a default `file.{ext}` is derived from MimeType using the `MimeTypes` library.

### 2. IChannelClient — add SendFileAsync

```csharp
Task<string?> SendFileAsync(string chatId, MediaUpload media,
    string? caption = null, string? replyToId = null, CancellationToken ct = default);
```

Returns external message ID. Sends a single message containing both the file and the caption text. How the channel achieves this is an implementation detail (see section 3).

### 3. Mattermost implementation

Mattermost requires two API calls to send a file with a caption in a single post:

**Step 1 — Upload the file (stores server-side, not yet visible in chat)**

`MattermostApiClient.UploadFileAsync(channelId, filename, data, contentType, ct)`
- `POST /api/v4/files` as `multipart/form-data`
- Fields: `channel_id` (string), `files` (binary with filename)
- Returns `string[]` of file IDs

**Step 2 — Create a post that attaches the file and carries the caption**

`MattermostApiClient.CreatePostAsync(channelId, message, replyToId, fileIds, ct)`
- `POST /api/v4/posts` as JSON
- Body: `{ channel_id, message, root_id, file_ids }`
- The `message` field carries the caption text
- The `file_ids` field attaches the uploaded files
- Result: a single post with both text and attached file(s)

**Orchestration — `MattermostClient.SendFileAsync`**

Implements `IChannelClient.SendFileAsync` by calling step 1 then step 2. The caption parameter flows into `CreatePostAsync`'s `message` field.

### 4. SendFilePlugin — agent tool

```csharp
[KernelFunction("send_file")]
[Description("Send a file from the workspace to the current chat. Path is relative to workspace (e.g. 'screenshots/page.png') or absolute within the workspace directory.")]
public async Task<string> SendFileAsync(
    [Description("File path relative to workspace")] string path,
    [Description("Optional message to send with the file")] string? caption = null,
    [Description("MIME type override (auto-detected from extension if omitted)")] string? mimeType = null)
```

- Uses `ResolveSafePath` pattern (same as `FileSystemPlugin`) — jailed to agent workspace
- Reads file from disk
- Infers MIME type from file extension via `MimeTypes` library if not provided
- Calls `ToolContext.Channel.SendFileAsync` with `MediaUpload` + caption
- Returns confirmation message to agent

### 5. WhatsApp

`SendFileAsync` throws `NotSupportedException` for now.

### 6. MIME type handling — MimeTypes NuGet package

Add `MimeTypes` source-only NuGet package to `Lis.Core`. Compiles into the assembly (zero runtime deps) and provides:

- `MimeTypes.GetMimeType("file.png")` → `"image/png"` (extension → MIME)
- `MimeTypes.GetMimeTypeExtensions("image/png")` → `[".png"]` (MIME → extension)

Database sourced from mime-db (IANA + Apache + nginx). Covers all common types: images, PDFs, HTML, Markdown, archives, etc.

Used in:
- `MediaUpload`: derive default filename extension from MimeType
- `SendFilePlugin`: auto-detect MIME type from file extension

## Security

- Files can only be sent from within the agent's workspace directory (same jail as FileSystemPlugin)
- No external URL fetching — agent must own the file data
- ToolAuthLevel follows existing conventions

## Out of Scope

- WhatsApp file sending (future)
- External URL image sending
- Inline image rendering in AI responses
