# Prompt

View and edit the agent's system prompt sections. Prompt sections are named blocks of markdown that together form the agent's system instructions.

## Tools

### prompt-list_prompt_sections(type?)
List all prompt sections.
- `type` (string, optional): `names` (default) for name + description summary, `full` for complete content of every section.

### prompt-get_prompt_section(name)
Get the full content of a specific section.
- `name` (string): Exact section name from prompt-list_prompt_sections.

### prompt-update_prompt_section(name, content, description?)
Create or update a prompt section.
- `name` (string): Section name. Creates it if it doesn't exist.
- `content` (string): Markdown text for the section body.
- `description` (string, optional): Short description of what this section does.
- Changes take effect on the next message (not the current one).

## Workflow

**View current prompt:**
1. `prompt-list_prompt_sections(type="names")` — see section overview
2. `prompt-get_prompt_section(name="personality")` — read specific section

**Add a new instruction:**
1. `prompt-list_prompt_sections(type="names")` — check existing sections
2. `prompt-update_prompt_section(name="coding-style", content="Always use TypeScript...", description="Code style preferences")`

## Common Errors

- **"Section not found"**: Use exact name from `prompt-list_prompt_sections`. Names are case-sensitive.
- **Changes not visible**: Prompt updates take effect on the *next* message, not the current conversation turn.
