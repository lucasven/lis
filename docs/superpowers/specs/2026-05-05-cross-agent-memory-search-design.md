# Cross-Agent Memory Search

## Summary

Add a new `search_agent_memories` function to `MemoryPlugin` that allows agents to search memories across all agents. The existing `search_memories` function remains unchanged.

## Motivation

Currently, each agent can only search its own memories (+ unscoped ones) via `search_memories`. There is no way for an agent to access memories stored by other agents. Cross-agent memory search enables agents to share knowledge without duplicating memories.

## New Function

### `search_agent_memories`

**Parameters:**

| Parameter     | Type               | Required | Default | Description                                      |
|---------------|--------------------|----------|---------|--------------------------------------------------|
| `query`       | `string`           | Yes      | —       | Search keyword or phrase                         |
| `agentName`   | `string?`          | No       | `null`  | Filter to a specific agent's memories            |
| `contactName` | `string?`          | No       | `null`  | Filter to a specific contact's memories          |
| `after`       | `DateTimeOffset?`  | No       | `null`  | Only memories created on or after this date      |
| `before`      | `DateTimeOffset?`  | No       | `null`  | Only memories created on or before this date     |
| `limit`       | `int`              | No       | `10`    | Max results to return (clamped to 1–100)         |
| `offset`      | `int`              | No       | `0`     | Number of results to skip (for pagination)       |

**Behavior:**

- When no `agentName` is provided: searches all agents' memories (no agent scoping).
- When `agentName` is provided: resolves via case-insensitive `ILike` lookup on the `agent` table. Returns an error message if the agent is not found.
- When `contactName` is provided: resolves via case-insensitive `ILike` lookup on the `contact` table. Returns an error message if the contact is not found.
- Date filters are additive and optional:
  - `after` only: memories from that date forward.
  - `before` only: memories up to that date.
  - Both: memories within the range.
  - Neither: no date filtering (all time).
- `limit` is clamped to the range 1–100. Values outside this range are adjusted silently.
- `offset` defaults to 0. Used with `limit` for pagination.
- Search strategy follows the same logic as `search_memories`:
  - If an `IEmbeddingGenerator` is registered: vector search via cosine distance.
  - Otherwise: PostgreSQL full-text search with ranking.
- Results include the owning agent's name in the output.

**Output format:**

```
#id: [AgentName] [ContactName] content
```

- `[AgentName]` is included when the memory has an associated agent. Omitted otherwise.
- `[ContactName]` is included when the memory has an associated contact. Omitted otherwise.
- Returns `"No memories found."` when there are no results.

**Attributes:**

- `[ToolSummarization(SummarizationPolicy.Summarize)]` — same as existing `search_memories`.
- `[ToolAuthorization(ToolAuthLevel.Open)]` — no auth gating.

## Changes to Existing Code

### `MemoryPlugin.cs`

**New `SearchParams` record:**

```csharp
private record SearchParams(
    long?            AgentId,
    long?            ContactId,
    DateTimeOffset?  After,
    DateTimeOffset?  Before,
    int              Limit,
    int              Offset,
    bool             ScopeToCurrentAgent
);
```

- `ScopeToCurrentAgent = true`: filters to calling agent's ID + unscoped memories (current `ScopeByAgent` behavior).
- `ScopeToCurrentAgent = false`: when `AgentId` is set, filters to that specific agent; when `null`, no agent filter at all.

**Generalize `VectorSearchAsync` and `FtsSearchAsync`:**

Both methods change their signature from `(LisDbContext, ..., string query, long? contactId)` to `(LisDbContext, ..., string query, SearchParams p)`.

Changes inside both methods:
1. Replace `ScopeByAgent(q)` call with inline logic based on `p.ScopeToCurrentAgent` and `p.AgentId`.
2. Add conditional `.Where()` clauses for `p.After` and `p.Before` on `CreatedAt`.
3. Replace `.Take(10)` with `.Skip(p.Offset).Take(p.Limit)`.
4. Add `.Include(m => m.Agent)` to load the agent navigation property (needed for output formatting).

**Update existing `SearchMemoriesAsync`:**

Constructs a `SearchParams` with `ScopeToCurrentAgent = true`, `Limit = 10`, `Offset = 0`, and `After`/`Before` as `null`. Passes it to the generalized search methods. External behavior is unchanged.

**Remove `ScopeByAgent` method:**

Its logic is absorbed into the search methods via `SearchParams.ScopeToCurrentAgent`.

### No other files change

- No new entities, migrations, or schema changes needed.
- No changes to `ToolContext`, `AgentEntity`, or `MemoryEntity`.
- No new DI registrations required.

## Testing

- Unit test: `SearchAgentMemoriesAsync` with no agent filter returns memories from multiple agents.
- Unit test: `SearchAgentMemoriesAsync` with `agentName` filter returns only that agent's memories.
- Unit test: `SearchAgentMemoriesAsync` with invalid `agentName` returns error message.
- Unit test: date filters (`after`, `before`, both, neither) produce correct results.
- Unit test: `limit` clamping (values below 1, above 100, within range).
- Unit test: pagination with `offset` and `limit`.
- Unit test: output format includes `[AgentName]` and `[ContactName]` when present.
- Unit test: existing `SearchMemoriesAsync` behavior is unchanged (regression).
