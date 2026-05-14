# docs-for-ai Format Specification

> Shared reference for gen-docs and update-docs skills. This defines the exact format AI documentation must follow.

## Directory Structure

```
docs-for-ai/
├── index.md              # Project index (REQUIRED)
├── pitfalls.md           # Pitfalls & conventions (REQUIRED)
└── chapters/             # Domain chapters (REQUIRED)
    ├── <domain>.md       # Simple domain: ≤2 services, single file
    ├── <domain>/         # Complex domain: ≥3 services, directory
    │   ├── _overview.md  # Domain overview + file inventory (underscore prefix = sorts first)
    │   ├── <service>.md  # One file per service
    │   └── ...
    └── workflows.md      # End-to-end workflows (REQUIRED)
```

### Split Rule

- **Single file** (`<domain>.md`): Domain contains ≤2 services → services as `##` headings
- **Directory** (`<domain>/`): Domain contains ≥3 services → `_overview.md` + per-service files
- Decision is made during analysis phase by counting services per domain, NOT by line count

---

## index.md Format

```markdown
# {Project Name} Project Index

> Read this first, then relevant chapter(s) per task.

## Overview

{One-sentence project description}

| Key      | Value |
|----------|-------|
| Stack    | {languages / frameworks / key dependencies} |
| Arch     | {architecture pattern, e.g. MVVM, MVC, microservices} |
| Platform | {runtime platform} |
| Tests    | {test framework + count} |
| Build    | {build command} |
| Test Cmd | {test command} |

## Directory Tree

{Annotated tree — key files only, use # comments for responsibilities}

## Architecture

{ASCII diagram showing layers and dependency direction}

## Dependency Impact Map

| Component | Affects |
|-----------|---------|
| {core component} | {modules/files affected by changes to it} |

## Chapters

| Chapter | File | When to read |
|---------|------|--------------|
| {domain} | `chapters/{file}` | {trigger condition} |
| Workflows | `chapters/workflows.md` | Any cross-component task |
```

### Rules for index.md
- Directory tree: show down to key file level, not every file
- Architecture diagram: ASCII art, show layers and call direction
- Dependency Impact Map: only include components where changes cascade non-obviously
- Chapter table: "When to read" should be actionable (e.g. "Adding/modifying tests", "OAuth, encryption, credentials")

---

## pitfalls.md Format

```markdown
# Pitfalls & Conventions

> Must-read before coding.

---

## {Module/Domain Name}

- 🔴 **{keyword}**: {Critical — violation causes crash, data loss, or security hole}
- **{keyword}**: {Normal — best practice, convention, or non-critical gotcha}
```

### Rules for pitfalls.md
- Group by module/domain
- 🔴 prefix ONLY for critical items (crash, data loss, security)
- Normal items have NO emoji prefix
- Each item: **bold keyword** + colon + one-line explanation
- Include "why" context when the reason is non-obvious
- Items that span multiple domains go under a dedicated section (e.g. "## Architecture Conventions")

---

## Chapter Format — Single File Domain (≤2 services)

```markdown
# {Domain Name} — {one-line description}

## Files

| File | Responsibility |
|------|----------------|
| `src/path/to/file` | {one-line responsibility} |

## {Service/Component Name}

### Overview
{2-3 lines: what it does, why it exists}

### Key Behaviors
- **{behavior}**: {description}

### Interface
`IServiceName` — `Method1(params)`, `Method2(params)`

### Internal Details
{Implementation details only needed when modifying this component}

### Dependencies
- Uses: {services it depends on}
- Used by: {services that depend on it}

---

## {Next Service/Component}
(same structure)
```

---

## Chapter Format — Directory Domain (≥3 services)

### _overview.md

```markdown
# {Domain Name} — {one-line description}

## Files

| File | Chapter | Responsibility |
|------|---------|----------------|
| `src/path/to/file` | [{service}.md]({service}.md) | {one-line responsibility} |

## Overview
{2-3 lines: domain-level context}
```

### Per-service file ({service}.md)

```markdown
# {Service Name} — {one-line description}

## Overview
{2-3 lines: what it does, why it exists}

## Key Behaviors
- **{behavior}**: {description}

## Interface
`IServiceName` — `Method1(params)`, `Method2(params)`

## Internal Details
{Implementation details only needed when modifying this component}

## Dependencies
- Uses: {services it depends on}
- Used by: {services that depend on it}
```

---

## workflows.md Format

```markdown
# Core Workflows

## {N}. {Workflow Name}

` ` `
{Pseudocode flowchart with indentation + arrows to show flow}
{Use ├── for branches, → for sequence, │ for continuation}
` ` `
```

### Rules for workflows.md
- Number each workflow
- Use pseudocode, not natural language paragraphs
- Show branching with tree characters (├── └──)
- Show sequence with → arrows
- Include error/edge case paths
- Each workflow should trace from user action to final state change

---

## General Writing Rules

1. **Token efficiency**: Use abbreviations, tables, inline code. Avoid filler words
2. **Paths**: Relative to project root (e.g. `src/Core/Services/Auth/OAuthService.cs`)
3. **Code references**: Use backticks for file names, class names, method names
4. **Tables over paragraphs**: When listing properties, configs, or mappings
5. **ASCII diagrams over descriptions**: For architecture, flow, and relationships
6. **No redundancy**: Do not repeat information across files. Each fact lives in exactly one place
7. **Concurrency patterns**: Always document thread safety mechanisms (locks, concurrent collections, etc.)
8. **Security-sensitive items**: Always document encryption schemes, key management, auth flows
