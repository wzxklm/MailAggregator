# 1. Environment

DevContainer (Ubuntu 22.04 + CUDA 12.4), manually built and ready to use.

# 2. Project Documentation

Documentation directory: `docs-for-ai/`

- `index.md` — project index: overview, directory tree, architecture diagram, chapter index
- `pitfalls.md` — project-specific pitfalls and coding conventions
- `chapters/` — detailed documentation (read on-demand by topic):
  - `core-data.md` — models, EF Core DbContext, data access
  - `auth.md` — credential encryption, password/OAuth auth, security architecture
  - `mail.md` — discovery, connection, sync, send, account management, SyncManager, concurrency
  - `desktop.md` — WPF UI layer, ViewModels, views, styles, DI registrations
  - `tests.md` — test structure, file list, test patterns
  - `workflows.md` — end-to-end workflow diagrams
  - `two-factor.md` — 2FA TOTP authenticator design & implementation guide

# 3. CI/CD

- CI/CD is tag-triggered: only `v*` tags (e.g. `v1.0.7`) trigger the GitHub Actions workflow
- To release: `git tag v<version> && git push origin v<version>`
- Workflow: `.github/workflows/build.yml` — builds on `windows-latest`, runs tests, publishes `win-x64` self-contained, uploads to GitHub Releases
- Regular `git push` to `main` does NOT trigger CI/CD

# 4. Current Feature Development

Branch: `feature/two-factor-auth` — 2FA TOTP authenticator
Design doc: `docs-for-ai/chapters/two-factor.md`

| Phase   | Scope                                                                            | Status      |
| ------- | -------------------------------------------------------------------------------- | ----------- |
| Phase 1 | Core data layer — enum, entity, DbContext, DatabaseInitializer, OtpNet package   | Completed   |
| Phase 2 | Core services — TwoFactorCodeService, TwoFactorAccountService, tests             | Completed   |
| Phase 3 | Desktop UI — DisplayItem, ViewModel, windows, MainWindow button, DI registration | Completed   |
| Phase 4 | Validation — `dotnet test` full test suite                                       | Not started |

Update this table after each phase is completed.

# 5. AI Workflow Rules

- Before starting any task, read `docs-for-ai/index.md` and `docs-for-ai/pitfalls.md`, then read only the relevant chapter(s) based on the task
- After completing any task, execute the following post-task workflow in order:
  1. **Code review**: Run `/simplify` to simplify code and check quality (runs automatically via subagent)
  2. **Doc sync**: Launch a subagent to run `git diff HEAD` (or `git diff` for unstaged changes), then update the relevant docs accordingly — `index.md` (directory tree, known issues), affected chapter files, and `pitfalls.md`
  3. **Pitfall review**: Back in the main session, review the entire task for any pitfalls or new conventions encountered, and update `docs-for-ai/pitfalls.md` under the appropriate module section
