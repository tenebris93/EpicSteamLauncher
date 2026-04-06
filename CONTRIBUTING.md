# Contributing

## Commit message standard

This repository enforces Conventional Commit subjects for pull requests and releases.

Required format:

`<type>(optional-scope): <description>`

Allowed types:

- `feat`
- `fix`
- `perf`
- `refactor`
- `docs`
- `test`
- `chore`

## Conventional commit type guide

Use the type that best describes the user-visible intent of the change.

| Type | Use when | Typical qualifiers | Avoid when |
|------|----------|--------------------|------------|
| `feat` | You add new behavior, commands, options, workflows, or capabilities. | New feature flag, new command, new integration behavior. | Pure bug fixes, docs-only changes, or internal cleanup with no new behavior. |
| `fix` | You correct incorrect behavior, crashes, regressions, or broken workflows. | Runtime bug fix, validation correction, path/IO fix, CI failure fix for broken behavior. | New functionality or broad code cleanup. |
| `perf` | You improve performance characteristics without changing intended behavior. | Faster startup, reduced allocations, lower CPU/IO for same result. | Functional bug fixes or refactors with no measurable performance goal. |
| `refactor` | You restructure code internally without changing externally expected behavior. | Class/method extraction, rename/move for maintainability, simplification. | Behavior changes (use `feat`/`fix`) or docs/test-only edits. |
| `docs` | You change documentation only. | README/CONTRIBUTING updates, comments that clarify usage/policy. | Code behavior changes or test/runtime updates. |
| `test` | You add or improve tests without production behavior changes. | New unit/integration tests, test coverage improvements, test fixtures. | Production code fixes/features (even if tests are included). |
| `chore` | Repository/maintenance work that does not change product behavior. | Dependency/tooling bumps, workflow wiring, formatting/config updates. | User-facing features or bug fixes. |

Selection rules:

- If behavior changes, prefer `feat` or `fix` over `refactor`/`chore`.
- If both code and docs are changed, choose the code intent (`feat`, `fix`, etc.), not `docs`.
- If both tests and code are changed, choose the code intent, not `test`.
- Use `chore(ci): ...` for workflow plumbing, and `fix(ci): ...` only when correcting broken CI behavior.

Quick decision tree:

- Does this add new user-visible behavior? -> `feat`
- Does this correct broken behavior? -> `fix`
- Is the main goal speed or resource efficiency? -> `perf`
- Is it code restructuring with no behavior change? -> `refactor`
- Is it documentation-only? -> `docs`
- Is it test-only? -> `test`
- Is it maintenance/tooling/config work with no behavior change? -> `chore`

Examples:

- `feat: add Epic game import command`
- `fix(steam): handle missing shortcuts.vdf`
- `docs: update release workflow notes`
- `chore(ci): add commit message lint workflow`

Notes:

- Use lowercase type.
- Scope is optional, but if present it should be lowercase and concise.
- The release changelog groups commits by type, so following this format keeps release notes readable.
- Merge and auto-generated revert subjects are allowed.

## Enforcement

Commit subjects are checked by:

- `.github/workflows/commit-message-lint.yml` on pull requests and pushes to `main`
- `.github/workflows/release.yml` before release notes are generated
- `.github/scripts/policy/validate-commit-messages.ps1` as the shared validation script used by both workflows

To make this mandatory in GitHub, set `Commit Message Lint / lint` as a required status check in branch protection for `main`.

