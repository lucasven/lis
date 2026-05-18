# Agent-to-Agent (A2A)

Discover and communicate with other agents. Delegate specialized tasks to agents with different capabilities.

## Tools

### a2a-list_agents()
List all available agents with name, description, and skill list.
- No parameters. Returns every registered agent.

### a2a-get_agent(name)
Get detailed information about a specific agent.
- `name` (string): Agent name as returned by a2a-list_agents.

### a2a-send(agent, message)
Send a message to another agent and receive their response.
- `agent` (string): Target agent name.
- `message` (string): The task or question to send. Be specific — the target agent has no context from this conversation.

The target agent processes the message independently with its own tools and system prompt. Returns the agent's text response.

## Workflow

1. `a2a-list_agents()` — discover available agents and what they can do
2. `a2a-get_agent(name="agent-name")` — (optional) get full details if needed
3. `a2a-send(agent="agent-name", message="Do X with Y")` — delegate the task

## Common Errors

- **"Agent not found"**: Use exact name from `a2a-list_agents()`. Names are case-sensitive.
- **Empty or unhelpful response**: The target agent has no conversation context. Include all necessary details in the message.
