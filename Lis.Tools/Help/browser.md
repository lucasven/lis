# Browser

Browser automation via PinchTab. Each agent gets its own persistent browser profile. The browser starts automatically on first navigate and closes when the conversation turn ends (cookies and logins persist across turns).

## Tools

### browser-navigate(url, headed?)
Open a URL. Starts the browser automatically on first use.
- `url` (string): Full URL including protocol (e.g. `https://example.com`).
- `headed` (bool, optional): Show a visible browser window (default false = headless). Not available without a display (e.g. Docker).

### browser-snapshot(filter?)
Read the page as an accessibility tree with element refs (`e0`, `e1`, ...).
- `filter` (string, optional): `interactive` (default, actionable elements only) or `full` (all content).
- Refs are used by browser-click, browser-type, and browser-press.
- Cheaper than screenshot for reading content.

### browser-click(ref)
Click an element by its ref.
- `ref` (string): Element ref from browser-snapshot (e.g. `e5`).

### browser-type(ref, text)
Fill a form field by its ref. Replaces existing content.
- `ref` (string): Element ref of the input field.
- `text` (string): Text to fill.

### browser-press(key, ref?)
Press a keyboard key, optionally on a specific element.
- `key` (string): e.g. `Enter`, `Tab`, `Escape`, `ArrowDown`.
- `ref` (string, optional): Element to focus first.

### browser-scroll(direction?)
Scroll the page.
- `direction` (string): `down` (default) or `up`.

### browser-screenshot()
Take a visual screenshot. More expensive in tokens than snapshot — prefer snapshot for text.

### browser-eval(code)
Execute JavaScript in the page context.
- `code` (string): JavaScript expression. Result returned as JSON.

### browser-tabs()
List all open tabs with title and URL.

## Workflow

**Read a page:**
1. `browser-navigate(url="https://example.com")` — browser starts automatically
2. `browser-snapshot(filter="full")` — read content

**Fill a form:**
1. `browser-navigate(url="https://example.com/form")`
2. `browser-snapshot()` — find refs of the inputs
3. `browser-type(ref="e3", text="user@example.com")`
4. `browser-type(ref="e4", text="secret")`
5. `browser-click(ref="e5")`
6. `browser-snapshot()` — verify result

## Common Errors

- **"No page open"**: Call `browser-navigate` first.
- **Ref not found**: The page changed. Re-run `browser-snapshot()` to get fresh refs.
- **"Headed mode is not available"**: No display on this system (e.g. Docker). Use headless (default).
- **Page didn't load**: Use `browser-snapshot()` or `browser-screenshot()` to inspect the current state.
