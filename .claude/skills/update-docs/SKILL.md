---
name: update-docs
description: Update docs-for-ai documentation after code changes. Use after completing a feature, bug fix, refactoring, or any code modification to keep AI documentation in sync with the codebase.
allowed-tools: Edit, Write, Bash(rm *), Bash(mkdir *)
---

# Update docs-for-ai Documentation

You are updating the existing `docs-for-ai/` documentation to reflect recent code changes. This is an incremental update — modify only what changed, do not rewrite unchanged sections.

## Format Specification

Read the format specification below before proceeding:

!`cat .claude/skills/doc-format-spec.md`

## Procedure

### Step 1: Identify What Changed

This skill is designed to be invoked within the same conversation where code changes were made. You already have full context of what was modified — do NOT run `git diff` or re-read changed files you already know about.

1. Recall all code changes made in the current conversation
2. Categorize the changes:
   - **New files/services**: Need new documentation
   - **Modified files/services**: Need doc updates
   - **Deleted files/services**: Need doc removal
   - **Renamed/moved files**: Need path updates
   - **New dependencies added**: Need Dependency Impact Map update
   - **New pitfalls discovered**: Need pitfalls.md update
   - **Workflow changes**: Need workflows.md update

### Step 2: Read Current Documentation

Read only the documentation files that are affected by the changes:
- Always read `docs-for-ai/index.md` (may need directory tree, chapter table, or impact map updates)
- Read the relevant chapter file(s) for modified domains
- Read `docs-for-ai/pitfalls.md` if conventions or gotchas were discovered
- Read `docs-for-ai/chapters/workflows.md` if cross-component flows changed

### Step 3: Update Documentation

Apply changes following these rules:

#### For New Services/Components

1. Determine which domain the new service belongs to
2. Check the current split status of that domain:
   - If domain was single-file (≤2 services) and now has ≥3 → **convert to directory**:
     - Create `chapters/<domain>/` directory
     - Move existing content into `_overview.md` + per-service files
     - Add new service file
     - Update `index.md` chapter table path
   - If domain is already a directory → add new `<service>.md`
   - If domain is single-file and stays ≤2 → add new `##` section
3. Update `index.md`:
   - Directory tree (add new files)
   - Dependency Impact Map (if the new component has cascade effects)
   - Chapter table (if new domain created)
4. Update `chapters/workflows.md` if the new service participates in user-facing workflows
5. Add relevant pitfalls to `pitfalls.md`

#### For Modified Services/Components

1. Read the current documentation for that service
2. Read the modified source code
3. Update only the sections that changed:
   - Key Behaviors: add/modify/remove behaviors
   - Interface: update method signatures
   - Internal Details: update implementation details
   - Dependencies: update if dependency graph changed
4. Update `index.md` if:
   - Architecture diagram affected
   - Dependency Impact Map affected
   - Test count changed
5. Update `pitfalls.md` if new gotchas discovered during the change
6. Update `workflows.md` if flow logic changed

#### For Deleted Services/Components

1. Remove from chapter file (or delete service file if in directory domain)
2. Check if domain should be **converted back to single file**:
   - If directory domain now has ≤2 services → consolidate into `chapters/<domain>.md`
   - Delete the directory
   - Update `index.md` chapter table path
3. Update `index.md`:
   - Directory tree (remove files)
   - Dependency Impact Map (remove entries)
   - Architecture diagram (if affected)
4. Remove from `pitfalls.md` (items that no longer apply)
5. Remove from `workflows.md` (steps that no longer exist)

#### For Renamed/Moved Files

1. Update all file path references across docs
2. Update `index.md` directory tree
3. Update Files tables in affected chapters

### Step 4: Consistency Check

After making all updates:
1. Verify `index.md` chapter table matches actual chapter files
2. Verify directory tree in `index.md` reflects current state
3. Verify no orphaned references (pointing to deleted/renamed files)
4. Verify split rule still holds (no domain with ≥3 services in a single file)
5. Verify test count in `index.md` overview table is current (run test command if unsure)

## Important Rules

- **Minimal changes**: Only modify documentation that corresponds to code changes. Do not rewrite unchanged sections
- **Read before editing**: Always read the current doc content before modifying it
- **Use conversation context**: You already have the full context of code changes from the current conversation — do not re-read files you already know about. Only read files you haven't seen in this conversation
- **No orphan docs**: If a service is deleted, its docs must be deleted too
- **Split rule is dynamic**: Domains can transition between single-file and directory as services are added/removed
- **Pitfalls are cumulative**: Add new ones as discovered, remove only when the underlying code is removed
- **Atomic updates**: Update all affected docs in one pass — do not leave docs partially updated
- **Paths are relative to project root**: e.g. `src/Core/Services/Auth/OAuthService.cs`
