# 1. Environment

DevContainer (Ubuntu 22.04 + CUDA 12.4), manually built and ready to use

# 2. Project Documentation

Documentation directory: `docs-for-ai/`

- `index.md` — project index: overview, directory tree, architecture diagram, chapter index
- `pitfalls.md` — project-specific pitfalls and coding conventions
- `chapters/` — detailed documentation (read on-demand by topic, see `index.md` for chapter index)

# 3. CI/CD

- CI/CD is tag-triggered: only `v*` tags (e.g. `v1.0.7`) trigger the GitHub Actions workflow
- To release: `git tag v<version> && git push origin v<version>`
- Workflow: `.github/workflows/build.yml` — builds on `windows-latest`, runs tests, publishes `win-x64` self-contained, uploads to GitHub Releases
- Regular `git push` to `main` does NOT trigger CI/CD

# 4. Current Feature Development

# 5. AI Workflow Rules

- Before starting any task, read `docs-for-ai/index.md` and `docs-for-ai/pitfalls.md`, then read only the relevant chapter(s) based on the task
