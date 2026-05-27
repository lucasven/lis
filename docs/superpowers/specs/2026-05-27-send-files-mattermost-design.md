# Send Files/Images to Mattermost

**Date:** 2026-05-27
**Status:** Approved

## Summary

Add file/image sending support to the Mattermost channel. Two-step flow: upload file via Mattermost API, then attach file IDs to a post. Expose this to AI agents via a `send_file` kernel function that reads files from the agent's workspace.

## Use Cases

- AI tools (browser screenshots, generated charts) sending output as images
- Media forwarding: image received from user, agent forwards it

## Design

### 1. MediaDownload — extend with optional Filename

```csharp
public sealed record MediaDownload(byte[] Data, string MimeType, string? Filename = null);
```

When `Filename` is null, a default `file.{ext}` is derived from MimeType (e.g. `image/png` → `file.png`).

### 2. IChannelClient — add SendFileAsync

```csharp
Task<string?> SendFileAsync(string chatId, MediaDownload media,
    string? caption = null, string? replyToId = null, CancellationToken ct = default);
```

Returns external message ID. Caption becomes the post text alongside the attachment (single post with both).

### 3. MattermostApiClient — two changes

**New: UploadFileAsync**
- `POST /api/v4/files` as `multipart/form-data`
- Fields: `channel_id` (string), `files` (binary with filename)
- Returns `string[]` of file IDs from the response

**Extend: CreatePostAsync**
- Add optional `string[]? fileIds` parameter
- When present, include `file_ids` array in the JSON body

### 4. MattermostClient.SendFileAsync

Implementation: upload via `UploadFileAsync`, then create post via `CreatePostAsync` with `file_ids` + caption text.

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
- Reads file from disk, infers MIME type from extension if not provided
- Calls `ToolContext.Channel.SendFileAsync` with the data
- Returns confirmation message to agent

### 6. WhatsApp

`SendFileAsync` throws `NotSupportedException` for now.

### 7. MIME type → extension mapping

Simple static helper for common types:
- `image/png` → `.png`
- `image/jpeg` → `.jpg`
- `image/gif` → `.gif`
- `image/webp` → `.webp`
- `application/pdf` → `.pdf`
- Fallback: `.bin`

Also reverse: extension → MIME type for `SendFilePlugin` auto-detection.

## Security

- Files can only be sent from within the agent's workspace directory (same jail as FileSystemPlugin)
- No external URL fetching — agent must own the file data
- ToolAuthLevel follows existing conventions

## Out of Scope

- WhatsApp file sending (future)
- External URL image sending
- Inline image rendering in AI responses
