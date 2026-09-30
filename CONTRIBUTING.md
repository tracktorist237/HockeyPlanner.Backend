# Contributing to Hockey Planner Backend

Backend changes follow the mandatory process documented in
[docs/governance/development-process.md](docs/governance/development-process.md).

Before editing:

1. Read the [Backend Constitution](docs/architecture/backend-constitution.md).
2. Select an issue in one approved
   [milestone](docs/roadmap/milestones.md).
3. Review its [Definition of Done](docs/roadmap/definition-of-done.md), relevant
   ADRs and Tech Debt entries.
4. Work in an issue-oriented task branch from current `develop` and keep the
   change small and reversible. Reuse a prepared task branch without resetting it.

Before opening a PR, run the required build/tests and complete the
[Code Review Checklist](docs/governance/code-review-checklist.md). Use the
repository PR template and record manual verification and rollback steps.

Open a Draft PR to `develop`, wait for mandatory CI, then request independent
review from a new Codex session. Self-review is not approval. A human merges
only after the current head has green CI and independent approval; completion
also requires develop validation, staging deploy and HP-75 smoke. The canonical
process defines substantial work, narrow exceptions and review evidence.
See [GitHub settings](docs/governance/github-workflow.md#develop-protection)
for the exact required check and operator setup; documentation does not enable
branch protection by itself.

Do not change the approved roadmap, Constitution or milestone criteria inside
an implementation PR.
