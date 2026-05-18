# Browser

Headless Chrome automation for interacting with web pages. Start a session, navigate, read content, and interact with page elements.

## Tools

### browser-start(url?)
Launch a headless Chrome browser.
- `url` (string, optional): Navigate to this URL immediately after launch.
- Only one browser session at a time. Must be called before other browser tools.

### browser-navigate(url)
Navigate to a URL in the current session.
- `url` (string): Full URL including protocol (e.g. `https://example.com`).

### browser-snapshot()
Get the page's DOM as accessible plain text (aria tree).
- Returns element selectors usable with browser-click and browser-type.
- Cheaper than screenshot for reading text content.

### browser-screenshot()
Take a visual screenshot of the current page.
- Returns an image. More expensive in tokens than snapshot — prefer snapshot for text.

### browser-click(selector)
Click an element on the page.
- `selector` (string): CSS selector. Use browser-snapshot to discover selectors.

### browser-type(selector, text)
Type text into a form field.
- `selector` (string): CSS selector of the input field.
- `text` (string): Text to type. The field is focused before typing.

### browser-evaluate(script)
Execute JavaScript in the page context.
- `script` (string): JavaScript code. Return value is converted to string.

### browser-tabs()
List all open tabs with title and URL.

### browser-close()
Close the browser session and free resources.

## Workflow

**Read a page:**
1. `browser-start(url="https://example.com")` — launch and navigate
2. `browser-snapshot()` — read content as text
3. `browser-close()` — clean up

**Fill a form:**
1. `browser-start(url="https://example.com/form")`
2. `browser-snapshot()` — find input selectors
3. `browser-type(selector="#email", text="user@example.com")`
4. `browser-type(selector="#password", text="secret")`
5. `browser-click(selector="button[type=submit]")`
6. `browser-snapshot()` — verify result
7. `browser-close()`

## Common Errors

- **"No browser session"**: Call `browser-start` first.
- **"Element not found"**: Selector is wrong. Use `browser-snapshot()` to see current page elements and their selectors.
- **Page didn't load**: Use `browser-snapshot()` or `browser-screenshot()` to inspect the current state.
