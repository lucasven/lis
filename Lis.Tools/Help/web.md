# Web

Search the web and fetch URL content as plain text.

## Tools

### web-search(query)
Search the web via Brave Search.
- `query` (string): Search query text.
- Returns: list of results with title, URL, and text snippet.

### web-fetch(url)
Fetch a URL and return its content as cleaned plain text.
- `url` (string): Full URL including protocol.
- HTML tags are stripped. Returns readable text content.
- For interactive or JavaScript-heavy pages, use browser tools instead.

## Workflow

**Research a topic:**
1. `web-search(query="semantic kernel plugins tutorial")` — find relevant pages
2. `web-fetch(url="https://example.com/article")` — read a specific result

**Read a specific URL:**
1. `web-fetch(url="https://api.example.com/docs")` — fetch directly if you already have the URL

## Common Errors

- **Empty content from fetch**: The page may require JavaScript to render. Use `browser-start` + `browser-snapshot` instead.
- **No search results**: Try rephrasing the query or using different keywords.
