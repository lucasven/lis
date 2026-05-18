# Skills

Manage installable prompt-based skills that extend the agent's capabilities with custom instructions.

## Tools

### skill-install(url)
Install a skill from a source URL or local path.
- `url` (string): Accepts:
  - GitHub repo: `https://github.com/user/repo` (looks for SKILL.md in root)
  - Raw .md URL: direct link to a markdown skill file
  - Local path: absolute filesystem path to a .md file
- The file must have YAML frontmatter with `name` and `description` fields.
- Re-installing an existing name updates it in place (no duplicates).
- Assets referenced in the skill are copied to the agent's workspace directory.

### skill-list(agent?)
List installed skills with name, description, and status.
- `agent` (string, optional): Target agent name. Defaults to current agent. Querying other agents requires owner permissions.

### skill-uninstall(name)
Permanently remove a skill.
- `name` (string): Exact skill name as shown in skill-list.

### skill-enable(name)
Enable a disabled skill. Enabled skills are included in the agent's system prompt.
- `name` (string): Skill name.

### skill-disable(name)
Disable a skill without removing it. It stays installed but won't load into the prompt.
- `name` (string): Skill name.

### skill-get(name)
View full details: content, metadata, enabled/disabled status, asset paths.
- `name` (string): Skill name.

### skill-use(name)
Activate a skill for the current conversation, loading its full instructions into context.
- `name` (string): Skill name. Must be installed and enabled.

## Workflow

1. `skill-install(url="https://github.com/user/skill-repo")` — fetch and store
2. `skill-list()` — verify it appears, check status
3. `skill-enable(name="my-skill")` — if not already enabled (skills are enabled by default after install)
4. `skill-use(name="my-skill")` — load instructions into current conversation
5. `skill-disable(name="my-skill")` / `skill-uninstall(name="my-skill")` — deactivate or remove

## Common Errors

- **"Skill not found"**: Name is case-sensitive. Use `skill-list()` to check exact names.
- **"No SKILL.md found"**: Source must contain a valid markdown file with `name`/`description` YAML frontmatter.
- **"Skill already exists"**: This is not an error — reinstalling updates the existing skill.
