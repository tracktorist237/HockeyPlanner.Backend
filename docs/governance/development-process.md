# Backend Development Process

Status: **Mandatory**

## Before starting

1. Select an issue assigned to one approved milestone.
2. Confirm that its scope is a small, complete and reversible change.
3. Read the Constitution, milestone DoD, relevant ADRs and debt entries.
4. Record explicit non-goals in the issue.
5. Use a separate branch for every milestone implementation or individual
   implementation task:
   - Codex branch: `codex/mNN-short-description`;
   - human contributor branch: `refactor/mNN-short-description`;
   - Codex must not create or switch branches without an explicit user command;
   - if the user has already prepared the branch, Codex continues in it;
   - a documentation review that makes no changes does not require a new branch.
6. Confirm that the baseline `dotnet build` and `dotnet test` pass, or document
   the known baseline failure before editing.

## Implementation cycle

Use the same cycle for every milestone and every issue inside it:

1. Implement one small coherent slice.
2. Preserve public API and business behavior unless the approved issue says
   otherwise.
3. Run a targeted build/test as soon as the slice compiles.
4. Run the complete required build and test suite.
5. Perform self-review using the Code Review Checklist.
6. Perform the manual smoke scenario listed in the issue and milestone DoD.
7. Update the Tech Debt Registry when debt was found, accepted or resolved.
8. Record actual results, CI links, manual checks and completion state in the
   corresponding GitHub milestone tracking issue.
9. Create or update an ADR only when the ADR rules apply.
10. Prepare a pull request using the repository template.
11. Stop after the issue is complete. Do not absorb adjacent cleanup.

## Required commands

```powershell
dotnet restore HockeyPlanner.Backend.sln
dotnet build HockeyPlanner.Backend.sln --no-restore
dotnet test HockeyPlanner.Backend.sln --no-build
git diff --check
git status --short
```

When M1 introduces dedicated test projects, targeted tests may run during
development, but the full solution test command remains the PR gate.

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

A PR is ready for review only when:

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
