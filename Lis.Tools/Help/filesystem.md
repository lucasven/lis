# Filesystem

Read, write, and search files within the agent's workspace directory.

## Tools

### fs-read_file(path, offset?, limit?)
Read file contents with line numbers.
- `path` (string): File path relative to workspace or absolute.
- `offset` (int, optional, default=1): Starting line number (1-based).
- `limit` (int, optional, default=200): Maximum lines to return.

### fs-write_file(path, content)
Write content to a file, creating parent directories as needed.
- `path` (string): File path relative to workspace or absolute.
- `content` (string): Full file content. Overwrites existing content.

### fs-edit_file(path, old_text, new_text)
Find and replace text in a file.
- `path` (string): File path.
- `old_text` (string): Exact text to find (first occurrence only).
- `new_text` (string): Replacement text.

### fs-list_directory(path, showHidden?)
List files and directories with sizes.
- `path` (string): Directory path relative to workspace or absolute.
- `showHidden` (bool, optional, default=false): Show hidden files and directories.
- Directories shown with trailing `/`, files with human-readable sizes.

### fs-search_files(pattern, path?)
Search for files matching a glob pattern recursively.
- `pattern` (string): Glob pattern (e.g. `*.json`, `**/*.md`).
- `path` (string, optional): Base directory. Defaults to workspace.

## Workflow

**Edit a file:**
1. `fs-read_file(path="config.json")` — read current content
2. `fs-edit_file(path="config.json", old_text="old value", new_text="new value")` — make the change
3. `fs-read_file(path="config.json")` — verify

**Explore a directory:**
1. `fs-list_directory()` — see workspace root
2. `fs-list_directory(path="src/")` — drill into a subdirectory
3. `fs-search_files(pattern="*.ts")` — find all TypeScript files

## Common Errors

- **"File not found"**: Check the path. Use `fs-list_directory` or `fs-search_files` to locate files.
- **"Text not found"**: The `old_text` in fs-edit_file must match exactly (including whitespace and newlines). Read the file first to copy the exact text.
