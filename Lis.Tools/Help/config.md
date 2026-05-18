# Config

Read and update agent and chat configuration. Manage access control via allowed senders.

## Tools

### cfg-get_agent_config(agent?)
Read an agent's configuration fields.
- `agent` (string, optional): Agent name. Defaults to current agent.
- Returns: model, max_tokens, context_budget, thinking_effort, tool_profile, display_name, and all other config fields.

### cfg-update_agent_config(key, value, agent?)
Update a single configuration field on an agent.
- `key` (string): One of: `model`, `max_tokens`, `context_budget`, `thinking_effort`, `tool_notifications`, `compaction_threshold`, `keep_recent_tokens`, `tool_prune_threshold`, `tool_keep_threshold`, `tool_summarization_policy`, `display_name`, `mention_triggers`, `group_context_prompt`, `tool_profile`, `tools_allow`, `tools_deny`, `workspace_path`, `exec_security`, `exec_timeout_seconds`.
- `value` (string): The new value. Type depends on the key (string, int, or bool).
- `agent` (string, optional): Target agent. Defaults to current agent.

### cfg-get_chat_config(chatId?)
Read a chat's configuration fields.
- `chatId` (string, optional): Chat ID. Defaults to current chat.

### cfg-update_chat_config(key, value, chatId?)
Update a single configuration field on a chat.
- `key` (string): One of: `enabled` (bool), `require_mention` (bool), `open_group` (bool), `group_context_messages` (int), `debounce_ms` (int), `agent` (name or "default").
- `value` (string): The new value.
- `chatId` (string, optional): Defaults to current chat.

### cfg-add_allowed_sender(senderId, chatId?)
Add a sender to the chat's allowed senders list.
- `senderId` (string): Sender's JID or identifier.
- `chatId` (string, optional): Defaults to current chat.

### cfg-remove_allowed_sender(senderId, chatId?)
Remove a sender from the allowed list.
- `senderId` (string): Sender's JID or identifier.
- `chatId` (string, optional): Defaults to current chat.

### cfg-list_allowed_senders(chatId?)
List all allowed senders for a chat.
- `chatId` (string, optional): Defaults to current chat.

### cfg-list_chats()
List all chats with their configuration. Owner-only.

## Workflow

**Change agent model:**
1. `cfg-get_agent_config()` — see current config
2. `cfg-update_agent_config(key="model", value="claude-sonnet-4-6-20250514")` — update

**Restrict a group chat:**
1. `cfg-update_chat_config(key="open_group", value="false")` — require allowlist
2. `cfg-add_allowed_sender(senderId="user@s.whatsapp.net")` — add permitted senders

## Common Errors

- **Invalid key**: Check the valid keys listed above for each update function.
- **"Agent not found"** / **"Chat not found"**: Verify the name/ID with cfg-list_chats or cfg-get_agent_config.
