---
name: gen-docs
description: Generate docs-for-ai documentation from scratch. Use when the user wants to create docs-for-ai for an existing project that has no AI documentation, or regenerate documentation when existing docs are outdated or non-conformant to the format specification.
allowed-tools: Agent, Write, Edit, Bash(rm *), Bash(mkdir *), Bash(rm -rf **)
---

# Generate docs-for-ai Documentation

You are generating a complete `docs-for-ai/` documentation set for an existing codebase. This documentation is designed for AI consumption — optimized for token efficiency, structured for on-demand loading, and focused on what AI needs to understand to safely modify the code.

**Context management**: All source code reading MUST happen inside subagents. The main conversation window only works with structured summaries and consolidated data — never raw source code.

## Format Specification

Read the format specification below before proceeding:

!`cat .claude/skills/doc-format-spec.md`

## Domain Summary Format

Phase 1b subagents MUST return their results in this exact format. This is also provided to Phase 3 subagents as input context.

```
## Domain: {name}
Services: {count} → split: file|directory

### {ServiceName} (`{file_path}`)
- **Role**: {1-2 line description}
- **Interface**: `IServiceName` — `Method1(params)→Ret`, `Method2(params)→Ret`
- **Key Behaviors**:
  - **{behavior}**: {description}
- **Uses**: {comma-separated list of services/components it depends on}
- **Threading**: {lock/semaphore/concurrent collection/none}
- **Security**: {encryption/auth/key management details, or "none"}

(repeat for each service in this domain)

### Internal Types
- `{TypeName}` (`{file_path}`): {1-line role}
(only for significant non-service types: models, enums, helpers worth documenting)

### Pitfalls
- {description} [🔴 if critical: crash/data-loss/security]

### Workflow Fragments
- **{workflow name}**: {this domain's role/steps in the workflow}
```

## Procedure

Follow these phases strictly in order. Do NOT skip phases or combine them.

### Phase 0: Clean Slate

If `docs-for-ai/` already exists, delete the entire directory first. This skill always generates from scratch — partial updates are handled by the `update-docs` skill instead.

### Phase 1a: Scout

**Execute in**: 1 subagent (subagent_type: `Explore`)

Launch a subagent to analyze the project's high-level structure and identify domains.

**Subagent prompt must instruct it to**:

1. Scan the full directory structure using Glob
2. Read build/config files (package.json, \*.csproj, Cargo.toml, go.mod, pom.xml, requirements.txt, etc.)
3. Read entry points (Main, Program.cs, Startup, DI registration, app initialization)
4. Read test project files to identify test framework and test patterns
5. Identify the project's language, framework, architecture pattern, platform
6. Identify logical domains by scanning source directories for groupings (by folder, namespace, module)
7. For each domain, list all source files that belong to it

**Require the subagent to return in this exact format**:

```
## Project Metadata
| Key | Value |
|-----|-------|
| Stack | {languages / frameworks / key deps} |
| Arch | {architecture pattern} |
| Platform | {runtime platform} |
| Tests | {test framework + location} |
| Build | {build command} |
| Test Cmd | {test command} |

## Directory Tree
{annotated tree — key directories and files, with # comments for responsibilities}

## Domains
### {Domain Name}
- **Description**: {what this domain covers}
- **Files**:
  - `{file_path}`
  - `{file_path}`
(repeat for each domain identified)
```

### Phase 1b: Domain Analyzers

**Execute in**: N subagents in parallel (subagent_type: `Explore`), one per domain from Phase 1a

For each domain identified by the Scout, launch a subagent to deeply analyze that domain's code.

**Each subagent prompt must include**:

1. The domain name and its complete file list (from Phase 1a)
2. The Domain Summary Format (copy the format defined above into the prompt)
3. Instructions to:
   - Read ALL source files in the assigned file list
   - Identify all services/components: their roles, public interfaces, key behaviors
   - Map internal dependencies (what each service Uses)
   - Note threading/concurrency mechanisms (locks, semaphores, concurrent collections)
   - Note security-relevant items (encryption, auth, key management)
   - Search for WARNING, HACK, WORKAROUND, NOTE, IMPORTANT, TODO comments
   - Identify this domain's role in any user-facing workflows
   - Return results in the exact Domain Summary Format

**Each subagent returns**: A domain summary in the Domain Summary Format.

**After ALL domain subagents complete**: Proceed to Phase 1c.

### Phase 1c: Consolidation

**Execute in**: Main conversation window

Using the Scout results (Phase 1a) and all domain summaries (Phase 1b), synthesize the following in your context:

1. **Split decisions**: For each domain, verify service count → ≤2 services = single file, ≥3 services = directory
2. **Global dependency graph**: From each service's "Uses" field, derive the reverse "Used by" mapping for every service
3. **Workflow list**: Stitch workflow fragments from all domains into complete end-to-end workflows, noting the full sequence of services involved
4. **Pitfall aggregation**: Collect all pitfalls from all domains, group by domain, preserve 🔴 markers
5. **Dependency Impact Map**: Identify components whose changes cascade to multiple other components

This consolidated analysis is the input for Phases 2–5. Keep it in your context.

### Phase 2: Write index.md

**Execute in**: Main conversation window — no source code reading

Using the consolidated analysis, write `docs-for-ai/index.md` following the format specification:

1. Overview table with project metadata (from Scout)
2. Annotated directory tree (from Scout, refined)
3. ASCII architecture diagram (synthesized from dependency graph)
4. Dependency Impact Map table
5. Chapters table (from domain mapping + split decisions)

### Phase 3: Chapter Writers

**Execute in**: N subagents in parallel (subagent_type: `general-purpose`), one per domain

For each domain, launch a subagent to read the source code and write the chapter documentation.

**Each subagent prompt must include**:

1. Instruction to first read the format spec file: `.claude/skills/doc-format-spec.md`
2. The domain summary from Phase 1b (for this specific domain)
3. The "Used by" reverse mapping for all services in this domain (from Phase 1c)
4. The split decision for this domain: single file or directory (from Phase 1c)
5. The complete file list for this domain
6. Instructions to:
   - Read the format spec file first to understand the exact documentation format
   - Read ALL source code files for this domain (read before writing — mandatory)
   - Write chapter file(s) to `docs-for-ai/chapters/` following the format spec exactly:
     - Single-file domain (≤2 services): write `docs-for-ai/chapters/{domain}.md`
     - Directory domain (≥3 services): create `docs-for-ai/chapters/{domain}/` with `_overview.md` + per-service `.md` files
   - Include accurate Dependencies sections with both "Uses" and "Used by"
   - Document threading, security, and concurrency patterns thoroughly
   - Return confirmation of files written

### Phase 4: Write workflows.md

**Execute in**: Main conversation window — no source code reading

Using the stitched workflows from Phase 1c, write `docs-for-ai/chapters/workflows.md`:

- Number each workflow
- Use pseudocode flowchart format (indentation + arrows + tree characters)
- Cover all major user-facing operations identified
- Include error/edge case branches
- Trace from user action to final state change

### Phase 5: Write pitfalls.md

**Execute in**: Main conversation window — no source code reading

Using the aggregated pitfalls from Phase 1c, write `docs-for-ai/pitfalls.md`:

- Group by module/domain
- Mark critical items (crash, data loss, security) with 🔴
- Each item: bold keyword + colon + explanation
- Include architecture conventions section for cross-cutting concerns

### Phase 6: Self-Review

**Execute in**: 1 subagent (subagent_type: `general-purpose`)

Launch a subagent to review and fix all generated documentation.

**Subagent prompt must include**:

1. Instruction to read ALL files under `docs-for-ai/`
2. Instruction to read `.claude/skills/doc-format-spec.md` for format compliance checking
3. Permission to read source code files if needed to verify documentation accuracy
4. The review checklist below

**Review checklist**:

1. **Redundancy**: Each fact lives in exactly one place across all files
2. **Cross-references**: Chapters table in index.md matches actual chapter files on disk
3. **File paths**: All paths are relative to project root and point to real files
4. **Dependencies**: Every service has both "Uses" and "Used by" filled in accurately
5. **Split correctness**: Domains with ≥3 services use directory format, ≤2 use single file
6. **Format compliance**: All files follow the format specification structure
7. **Completeness**: No services or significant components are undocumented

**The subagent fixes any issues found directly** — it does not just report them. It returns a summary of changes made (or confirms no issues found).

## Important Rules

- **No source code in main window**: All source code reading happens in subagents (Phase 1a, 1b, 3, 6). The main window only handles structured summaries, consolidated data, and writing docs from that data
- **Read before writing**: Subagents in Phase 3 MUST read actual source code before writing documentation — never guess from file names or summaries alone
- **Token efficiency**: Use tables, abbreviations, inline code. No filler
- **No opinions**: Document what IS, not what SHOULD BE
- **Security items are mandatory**: Always document encryption, auth flows, key management
- **Concurrency items are mandatory**: Always document thread safety mechanisms
- **Test patterns are mandatory**: Always document test framework, patterns, mocking strategy
- **Paths are relative to project root**: e.g. `src/Core/Services/Auth/OAuthService.cs`
- **Do not document docs-for-ai itself**: It is meta-documentation, not part of the project
