# Web

Search the web.

## Tools

### web-search(query)
Search the web via Brave Search.
- `query` (string): Search query text.
- Returns: list of results with title, URL, and text snippet.

## Workflow

**Research a topic:**
1. `web-search(query="semantic kernel plugins tutorial")` — find relevant pages
2. `browser-navigate(url="https://example.com/article")` + `browser-snapshot(filter="full")` — read a specific result

## Common Errors

- **No search results**: Try rephrasing the query or using different keywords.
- **"Web search is not configured"**: Requires `LIS_WEB_SEARCH_ENABLED=true` and `LIS_WEB_SEARCH_API_KEY`.
