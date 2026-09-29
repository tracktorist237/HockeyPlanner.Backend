# Backend Development Process

Status: **Mandatory**

## Before starting

1. Select an issue assigned to one approved milestone.
2. Confirm that its scope is a small, complete and reversible change.
3. Read the Constitution, milestone DoD, relevant ADRs and debt entries.
4. Record explicit non-goals in the issue.
5. Start an issue-oriented task branch from clean, current `develop`:
   `<type>/<issue>-short-description`, for example
   `quality/hp-76-pr-workflow` or `fix/hp-70-attendance-conflict`.
   An already prepared task branch is valid; preserve its history. Do not switch
   over unknown local changes. Codex needs user authorization to create/switch
   branches; a request to implement a task under this process authorizes its
   task branch. Read-only reviews need no new branch.
6. Establish the relevant baseline under Required commands below; for runtime
   work confirm `dotnet build` and `dotnet test`, or document an existing failure.

## Canonical implementation cycle

1. Define the issue, approved scope and non-goals.
2. Prepare the task branch from current `develop` as above.
3. Implement only that scope in the task branch, preserving other behavior.
4. Run local verification and self-review the complete diff.
5. Commit and push the task branch, never force-push.
6. Open a **Draft PR targeting develop**, using the repository template.
7. Wait for mandatory PR CI to pass; do not bypass a failed check.
8. Hand off to a **new Codex session or separate review agent** for independent
   review under the contract below.
9. The author addresses findings in the task branch and records dispositions.
10. Require green CI and independent `APPROVE` evidence for the final head SHA.
    New commits invalidate the earlier head approval; request re-review.
11. A human decides whether to mark ready and merge. Codex does not merge as
    part of implementation/review. Branch settings must also be configured.
12. Require `develop` validation after merge.
13. Wait for staging deployment, dependent on that validation.
14. Require HP-75 post-deploy staging smoke. Record any issue-specific manual
    checks that automation cannot prove.
15. Only then record completion evidence and mark the issue Done. Stop without
    starting adjacent work.

This process does not authorize changes to `master`, releases, production,
`VERSION`, or deployment settings. See [GitHub workflow](github-workflow.md)
for enforcement and the existing validation/deploy graph.

## Substantial changes and exceptions

Substantial changes include runtime/business code, API contracts, auth/security,
database/migrations, background jobs, concurrency/idempotency/retries,
notifications, integrations, dependencies, CI/CD, infrastructure, cross-cutting
refactors and non-trivial test architecture. Direct substantial pushes to
`develop` are prohibited by default, even when tests pass.

The only narrow exceptions are explicitly named typo/docs-only corrections,
obvious mechanical metadata corrections, or emergency hotfixes explicitly
authorized by Sergey. Record the exception, scope and authorization in the
issue/PR. An exception never bypasses failing CI or repository protection;
emergency work still needs retrospective independent review and tests.
It does not change `master` or release policy. HP-76 itself follows the full PR
process, not the docs-only exception.

## Independent review contract

Self-review is preparation, **not independent review**. The implementation
session must not approve its own work. Use a new Codex session or a separate
review agent without the author's implementation context as its only evidence.

The author supplies: issue, scope/non-goals, base and head SHAs, changed files,
API/database/configuration impact, commands/results, known warnings, risks and
rollback. Link this handoff and the reviewer session/report in the PR. Never
include credentials, tokens or sensitive logs.

The reviewer independently verifies the base/head SHAs, reads the **complete
diff**, affected production code, tests and workflows, and checks the author's
claims against the actual implementation. A summary or green CI is not proof
of correctness. Review correctness, security, contracts, database/migrations,
concurrency/races, idempotency/retries, test quality/flakiness and CI/deploy
safety. Explicitly explain dimensions that are not applicable.

Findings need severity, file/line evidence, impact and an actionable correction
or decision. Record one verdict, without numeric scores/rankings:

- `APPROVE`
- `CHANGES REQUIRED`
- `BLOCKED / NEEDS HUMAN DECISION`

PR evidence must identify the separate reviewer/session, reviewed head SHA and
verdict. `Pending` is an honest draft state, never approval. The human merger
checks that review and CI cover the current head and conversations are resolved;
this procedural Codex evidence is not a second human GitHub approval requirement.

## Required commands

```powershell
dotnet restore HockeyPlanner.Backend.sln
dotnet build HockeyPlanner.Backend.sln --no-restore
dotnet test HockeyPlanner.Backend.sln --no-build
git diff --check
git status --short
```

Targeted tests run during development; the reusable CI quality gate runs the
full solution suite, migration/contract checks and Debug/Release builds.
For documentation-only local work, inspect links and rule consistency, run
relevant governance/workflow checks and `git diff --check`; document why costly
runtime tests are not applicable. Follow stricter issue-specific checks. This
local risk-based choice never waives mandatory PR CI. Workflow/script changes
need meaningful trigger, dependency and secret-isolation tests, not snapshots.

## Documentation update rules

### Create an ADR when

- a decision changes or interprets a cross-cutting constitutional rule;
- public API, schema compatibility or transaction semantics are decided;
- an external provider lifecycle or security policy is selected;
- multiple reasonable alternatives have materially different consequences;
- an intentional long-term exception to the Constitution is accepted.

Do not create an ADR for local implementation details or a reversible rename.

### Update the Tech Debt Registry when

- a confirmed problem is deliberately left outside the current issue;
- a temporary Constitution exception is introduced;
- known debt changes priority, ownership, milestone or status;
- an issue resolves or supersedes a registered debt item.

Every entry needs evidence in a real file/method and an existing milestone or
`Deferred`. Do not create a milestone from a debt entry.

### Definition of Done

The DoD document remains static during implementation. Store actual results,
CI links, manual checks and completion state in the corresponding GitHub
milestone tracking issue. Change DoD only when an explicitly approved roadmap
decision changes a criterion, using a separate approved documentation PR.

### Update the Constitution or Principles when

Only after an explicit architectural decision and approved ADR. Ordinary
refactoring must conform to them rather than edit them.

## Pull request gate

A Draft PR may record pending checks honestly. It is ready for review only when:

- it is linked to one issue and one approved milestone;
- scope and non-goals are clear;
- build and required tests pass;
- manual verification is recorded;
- self-review checklist is complete;
- API/schema/config changes are explicitly declared;
- milestone tracking evidence and ADR, debt or DoD-criteria impacts are
  recorded;
- rollback instructions are practical;
- unrelated formatting, generated files and refactoring are absent.
