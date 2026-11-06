# Image

Generate or edit images using the ChatGPT subscription (Codex OAuth). Available only when the Codex provider is enabled.

## Tools

### image-generate(prompt, size?, quality?, background?, referencePaths?, count?, send?, caption?)
Generate an image and save it to `images/` in the workspace.
- `prompt` (string): Detailed description of the image, or of the edit to apply to the references.
- `size` (string, optional): `1024x1024`, `1536x1024` (landscape), `1024x1536` (portrait) or `auto`.
- `quality` (string, optional): `low`, `medium`, `high` or `auto`.
- `background` (string, optional): `transparent`, `opaque` or `auto`. Use `transparent` for logos/stickers.
- `referencePaths` (string, optional): Comma-separated workspace image paths. When set, the images are edited/composed instead of generated from scratch (max 5, 20 MB each).
- `count` (int, optional, default=1): Number of images (1-4).
- `send` (bool, optional, default=true): Send the result to the current chat.
- `caption` (string, optional): Message sent with the (first) image.
- Returns: workspace paths of the saved images.

## Workflow

**Create an image:**
1. `image-generate(prompt="watercolor of a lighthouse at dusk", size="1536x1024")` — generated and sent to chat

**Edit an existing workspace image (e.g. a previous result):**
1. `image-generate(prompt="add a red hat to the cat", referencePaths="images/20260101-120000-abc123.png")`

**Generate without sending, then send later:**
1. `image-generate(prompt="...", send=false)` → returns `images/<file>.png`
2. `sendfile-send_file(path="images/<file>.png", caption="...")`

## Common Errors

- **"Codex is not authenticated"**: Run `/auth codex` to log in.
- **"Image generation error: ... 429"**: Subscription image quota reached — try again later.
- **"reference image not found"**: Paths are relative to the workspace; check with `fs-list_directory`.
