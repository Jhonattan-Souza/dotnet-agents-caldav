---
name: caldav-tasks
description: >
  Manage CalDAV task lists (VTODO) — shopping lists, work tasks, personal goals, chores,
  or any list a user wants. Guides agents through the chat-oriented CalDAV MCP tools for
  discovering, reading, creating, completing, and deleting tasks by user-facing list names
  instead of raw hrefs. Emphasizes caching list metadata so subsequent lookups are fast.
  Use this skill whenever the user mentions tasks, to-dos, checklists, shopping lists,
  chores, goals, reminders, or any kind of list they want to track — even if they don't
  say "CalDAV" or "task list" explicitly. Also use when the user asks to organize, track,
  or manage anything that could be represented as a list of items with status.
---

# CalDAV Tasks Skill

This skill helps you work with CalDAV VTODO (task) data through MCP tools. Users can have any kind of lists — shopping lists, work backlogs, personal goals, household chores, reading lists, fitness tracking, or anything else. Your job is to make interacting with these lists feel natural and fast.

## Two tiers of tools

The MCP server exposes two styles of tools. The **chat-oriented** tools are what you'll use almost always — they accept user-facing display names like "Shopping" or "Work" and resolve them automatically. The **href-based** tools are lower-level and require absolute server paths.

### Chat-oriented tools (prefer these)

| Tool | What it does |
|---|---|
| `list_task_lists` | Discover all available task lists and which is the default |
| `show_tasks` | List tasks in a named list (or the default list if no name given) |
| `add_task` | Create a task in a named list (or the default list) |
| `find_tasks` | Search for tasks by exact summary across one or all lists |
| `complete_task_by_summary` | Mark a task as done by its summary text |
| `delete_task_by_summary` | Remove a task by its summary text |

### Href-based tools (only when the user provides an explicit href)

| Tool | What it does |
|---|---|
| `list_tasks` | Query tasks in a list by absolute href |
| `get_task` | Fetch a single task by absolute href |
| `create_task` | Create a task in a list by absolute href |
| `update_task` | Partial update of a task by absolute href |
| `complete_task` | Mark a task done by absolute href |
| `delete_task` | Delete a task by absolute href |

## Workflow

### 1. Discover lists first

At the start of a conversation involving tasks, call `list_task_lists`. This returns all available lists with their display names and which one is the default. Cache this in your conversation context and/or memory — you'll use it to route every subsequent call without re-fetching.

The response looks like:
```json
[
  {"href": "/calendars/user/shopping/", "displayName": "Shopping", "isDefault": false},
  {"href": "/calendars/user/tasks/", "displayName": "Tasks", "isDefault": true}
]
```

You don't need the hrefs when using chat-oriented tools — just pass the `displayName` as `taskListName`.

### 2. Reading tasks

Use `show_tasks` with the list's display name. If the user just says "my tasks" or "task list" without naming one, omit `taskListName` — the tool uses the configured default list automatically.

You can filter by:
- **status** — `NeedsAction` (to-do), `InProcess` (in progress), `Completed`, `Cancelled`
- **dueBefore / dueAfter** — date range for due dates
- **textSearch** — free-text search across summary and description
- **category** — filter by tag/category

Common patterns:
- "What do I need to do?" → `show_tasks` with no filters (shows everything)
- "What's on my shopping list?" → `show_tasks` with `taskListName="Shopping"`
- "What's due this week?" → `show_tasks` with `dueAfter` and `dueBefore`
- "Find my task about taxes" → `show_tasks` with `textSearch="taxes"`

### 3. Adding tasks

Use `add_task`. Pass the list name (or omit for default) and the summary. You can optionally set:
- **description** — longer details
- **priority** — `None`, `High`, `Medium`, `Low`
- **due** — due date/time
- **status** — `NeedsAction` (default), `InProcess`, `Completed`, `Cancelled`

Important: the `taskListName` parameter should match the user's intent, not the task content. If the user says "add eggs to my shopping list", the list is "Shopping" — don't guess the list from what the task sounds like. If they don't name a list, use the default.

### 4. Completing and deleting tasks

Use `complete_task_by_summary` or `delete_task_by_summary`. These search by exact summary match within the named list (or across all lists if no list is named).

These tools are **single-target** — if multiple tasks share the same summary, they return an "ambiguous" status with candidates instead of acting. When this happens, ask the user to clarify which list or which specific task.

If no task matches, they return "not_found" with the available list names — use this to help the user find the right list.

### 5. Updating tasks

For partial updates (changing due date, priority, description, etc.), use `update_task` which requires the absolute href. To get the href, first `find_tasks` to locate the task, then use the returned href for the update.

## Caching list metadata

After calling `list_task_lists` once, remember the list names for the rest of the conversation. This avoids redundant API calls and makes your responses faster. The key info to cache:

- **Display names** — what the user calls each list
- **Which is default** — so you can omit the list name when appropriate
- **Hrefs** — useful if you need to fall back to href-based tools

If a user references a list name that isn't in your cache, call `list_task_lists` again — they may have created a new list.

## Handling ambiguity

When `complete_task_by_summary` or `delete_task_by_summary` returns an "ambiguous" result, it includes candidate matches with their list names and hrefs. Present these to the user and ask them to specify which one they mean. Don't guess.

When the resolver can't find a list by name, it throws with candidate suggestions. Read the error — it tells you which lists are available so you can help the user pick the right one.

## Task status and priority values

**Status** (for filters and creation):
- `NeedsAction` — not started yet (default for new tasks)
- `InProcess` — currently being worked on
- `Completed` — finished
- `Cancelled` — no longer relevant

**Priority** (for creation and updates):
- `None` — no priority set (default)
- `High` — important/urgent
- `Medium` — normal priority
- `Low` — can wait

## Common user scenarios

### Shopping lists
"Add milk to my shopping list" → `add_task` with `taskListName="Shopping"`, `summary="milk"`
"Bought the eggs" → `complete_task_by_summary` with `summary="eggs"`, `taskListName="Shopping"`

### Work tasks
"What's on my plate?" → `show_tasks` on the default or "Work" list, filtered to `status="NeedsAction"`
"Mark the quarterly report as in progress" → Use `find_tasks` to get the href, then `update_task` with `status="InProcess"`

### Personal goals
"Show me my goals" → `show_tasks` on the "Goals" list
"Add: run a marathon" → `add_task` with `taskListName="Goals"`, `summary="Run a marathon"`

### Household chores
"What chores are left?" → `show_tasks` with `status="NeedsAction"` on the "Chores" list
"Done with laundry" → `complete_task_by_summary` with `summary="laundry"`

### Overdue tasks
"What's overdue?" → `show_tasks` with `dueBefore=<current date/time>` and `status="NeedsAction"` to find anything due but not yet done

## Tips for natural interaction

- Users rarely say "task list" — they might say "my list", "the grocery list", "things to do", "what's pending". Match their language to the right list name.
- When a user says "add X", they almost always mean the default list unless they name one. Don't ask "which list?" every time — only ask when it's genuinely ambiguous.
- When completing tasks, users often say things like "done with X" or "finished X" rather than "mark X as completed". Recognize these as completion requests.
- If a user asks about "all my tasks" or "everything I need to do", consider querying across multiple lists or using `find_tasks` without a list name to search everywhere.
- When showing tasks, group them meaningfully — by list, by due date, or by priority — rather than dumping a flat list.
