# 1. Development Environment

Running inside a DevContainer (Ubuntu 22.04, CUDA 12.4). See `.devcontainer/Dockerfile` and `docker-compose.yml` for details.

# 2. Project Documentation

- Requirements: docs/requirements.md
- Architecture: docs/architecture.md
- Task Breakdown: docs/tasks.md (AI-oriented, tasks ordered by dependencies)

# 3. Development Progress

- Current phase: Phase 3 (Mail Protocol Layer)
- Completed: Phase 0 — .NET 8 SDK, solution skeleton, NuGet dependencies, directory structure; Phase 1 — Data models, EF Core SQLite DbContext, AES-256-GCM credential encryption; Phase 2 — AutoDiscovery service (5-level fallback), OAuth PKCE service, Password auth service
- In progress: Pending Phase 3 start

# 4. Environment Status

- OS: Ubuntu 22.04 (x86_64, DevContainer)
- Git: 2.34.1
- Node.js: 22.x
- Python: 3.x (system)
- .NET SDK: 8.0.418
- NuGet packages: Configured (MailKit, EF Core SQLite, Serilog, CommunityToolkit.Mvvm, etc.)
- Project buildable: Yes (Core + Tests on Linux; Desktop is net8.0-windows, requires EnableWindowsTargeting on Linux)
- Tests passing: 85/85

# 5. Coding Conventions

- Core project TargetFramework: `net8.0` (cross-platform, buildable and testable on Linux)
- Desktop project TargetFramework: `net8.0-windows` (Windows only, cannot build on Linux)
- Use `async/await` for async methods, never create threads manually
- Interface naming: `I` prefix (e.g. `IEmailSyncService`)
- Sensitive data (passwords, tokens) must be encrypted before storage, never store in plaintext
- Use Serilog for logging, log all key operations (connections, sync, errors)

# 6. AI Workflow Rules

- Follow the phase order defined in docs/tasks.md
- Phases marked [parallel] can use subagents for concurrent development; [sequential] phases must run in order
- Run `/simplify` after completing each task for code quality review
- Run `/security-review` after each git commit
- Update "Development Progress" and "Environment Status" sections in this file after each phase
