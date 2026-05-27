# Send Files/Images to Mattermost

**Date:** 2026-05-27
**Status:** Approved

## Summary

Add file/image sending support to the Mattermost channel. Two-step flow: upload file via Mattermost API, then attach file IDs to a post. Expose this to AI agents via a `send_file` kernel function that reads files from the agent's workspace.

## Use Cases

- AI tools (browser screenshots, generated charts) sending output as images
- Media forwarding: image received from user, agent forwards it

## Design

### 1. MediaUpload — new record extending MediaDownload

`MediaDownload` stays unchanged (used for receiving). New `MediaUpload` extends it with an optional `Filename` for sending:

```csharp
// Existing — unchanged
public record MediaDownload(byte[] Data, string MimeType);

// New — for outbound files
public sealed record MediaUpload(byte[] Data, string MimeType, string? Filename = null)
    : MediaDownload(Data, MimeType);
```

When `Filename` is null, a default `file.{ext}` is derived from MimeType using the `MimeTypes` library.

`MediaDownload` is unsealed (was `sealed`) to allow inheritance.

### 2. IChannelClient — add SendFileAsync

```csharp
Task<string?> SendFileAsync(string chatId, MediaUpload media,
    string? caption = null, string? replyToId = null, CancellationToken ct = default);
```

Returns external message ID. Caption becomes the post text alongside the attachment (single post with both text and file).

### 3. MattermostApiClient — two changes

**New: UploadFileAsync**
- `POST /api/v4/files` as `multipart/form-data`
- Fields: `channel_id` (string), `files` (binary with filename)
- Returns `string[]` of file IDs from the response
- Upload only stores the file server-side; it is NOT visible in chat until attached to a post

**Extend: CreatePostAsync with file_ids**

The Mattermost `Post` model supports a `file_ids` field. After uploading via `UploadFileAsync`, the returned file IDs are passed to `CreatePostAsync` to attach them to a message.

- Add optional `string[]? fileIds` parameter to `CreatePostAsync`
- When present, include `file_ids` array in the JSON payload alongside `channel_id`, `message`, and `root_id`

### 4. MattermostClient.SendFileAsync

The Mattermost upload endpoint (`POST /api/v4/files`) only accepts `channel_id` and the binary file — no caption. The caption is sent as the `message` field in the subsequent `CreatePostAsync` call, which ties everything together:

1. Upload file via `UploadFileAsync(channelId, filename, data, contentType)` → returns file IDs
2. Create post via `CreatePostAsync(channelId, message: caption, fileIds: [...], replyToId)` → the `message` field carries the caption, `file_ids` attaches the uploaded file
3. Result: single post in chat with both the attached file and the caption text

### 5. SendFilePlugin — agent tool

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

### 6. WhatsApp

`SendFileAsync` throws `NotSupportedException` for now.

### 7. MIME type handling — MimeTypes NuGet package

Add `MimeTypes` source-only NuGet package to `Lis.Core`. It compiles into the assembly (zero runtime deps) and provides:

- `MimeTypes.GetMimeType("file.png")` → `"image/png"` (extension → MIME)
- `MimeTypes.GetMimeTypeExtensions("image/png")` → `[".png"]` (MIME → extension)

Database sourced from mime-db (IANA + Apache + nginx). Covers all common types: images, PDFs, HTML, Markdown, archives, etc. No hand-rolled mapping needed.

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
