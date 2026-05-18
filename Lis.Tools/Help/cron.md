# Cron (Scheduled Tasks)

Create and manage scheduled tasks that run on a cron schedule. Tasks can either prompt the AI or send messages directly to the chat.

## Tools

### cron-cron_create(name, cronExpression, payload, type?, timezone?)
Create a new scheduled task.
- `name` (string): Human-readable label for the task.
- `cronExpression` (string): Standard 5-field cron expression. Examples:
  - `0 9 * * 1-5` — weekdays at 9:00 AM
  - `0 */2 * * *` — every 2 hours
  - `30 8 1 * *` — 1st of each month at 8:30 AM
  - `0 0 * * 0` — every Sunday at midnight
- `payload` (string): The text content to send when triggered.
- `type` (string, optional, default: "prompt"): Either `prompt` or `message`.
  - `prompt`: payload is sent as a user message to the AI, which processes and responds
  - `message`: payload is sent directly to the chat as-is, no AI processing
- `timezone` (string, optional, default: UTC): IANA timezone (e.g. "America/Sao_Paulo").

### cron-cron_list()
List all scheduled tasks for the current chat.
- Returns: ID, name, cron expression, type, payload, enabled status, and next run time.

### cron-cron_delete(id)
Delete a scheduled task.
- `id` (int): Task ID from cron-cron_list.

### cron-cron_update(id, cron?, payload?, enabled?)
Update an existing scheduled task.
- `id` (int): Task ID from cron-cron_list.
- `cron` (string, optional): New cron expression.
- `payload` (string, optional): New payload text.
- `enabled` (bool, optional): Toggle on/off without deleting.

## Workflow

1. `cron-cron_create(name="Morning briefing", cronExpression="0 9 * * 1-5", type="prompt", payload="Give me a morning briefing")` — create task
2. `cron-cron_list()` — verify it was created, note the ID
3. `cron-cron_update(id=1, enabled=false)` — pause it temporarily
4. `cron-cron_delete(id=1)` — remove permanently

## Common Errors

- **Invalid cron expression**: Use standard 5-field cron syntax (minute hour day month weekday). Seconds are not supported.
- **"Task not found"**: Use `cron-cron_list()` to get valid IDs for the current chat.
