# Memory

Store and retrieve memories using vector similarity and full-text search. Memories can be linked to people for filtered retrieval.

## Tools

### mem-create_memory(content, contactName?)
Store a new memory.
- `content` (string): The text to remember. Will be embedded for vector search.
- `contactName` (string, optional): Link this memory to a person by name.

### mem-search_memories(query, contactName?)
Search memories by keyword or phrase.
- `query` (string): Search text. Matched via vector similarity and full-text search.
- `contactName` (string, optional): Filter results to memories linked to this person by name.
- Returns: memories ranked by relevance with their IDs, content, person, and timestamps.

### mem-update_memory(id, content)
Update an existing memory's content.
- `id` (int): Memory ID from search results.
- `content` (string): New text content. Re-embeds for vector search.

### mem-delete_memory(id)
Permanently delete a memory.
- `id` (int): Memory ID from search results.

## Workflow

1. `mem-create_memory(content="Lucas prefers dark mode", contactName="Lucas")` — store
2. `mem-search_memories(query="preferences", contactName="Lucas")` — retrieve
3. `mem-update_memory(id=42, content="Lucas prefers light mode now")` — update
4. `mem-delete_memory(id=42)` — remove if no longer relevant

## Common Errors

- **No results**: Try broader search terms. Vector search finds semantically similar content, not just exact matches.
- **"Memory not found"**: The ID may be wrong. Search again to get current IDs.
