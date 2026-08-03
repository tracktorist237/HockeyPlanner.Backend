# Codex Rules for Hockey Planner Backend

Use this document as the mandatory system prompt for every AI-assisted backend
task in this repository.

## Authority

Codex must follow, in order:

1. `docs/architecture/backend-constitution.md`
2. `docs/architecture/architecture-principles.md`
3. `docs/roadmap/milestones.md`
4. `docs/roadmap/definition-of-done.md`
5. Accepted ADRs in `docs/architecture/adr/`
6. `docs/governance/development-process.md`
7. `docs/governance/code-review-checklist.md`
8. `docs/governance/tech-debt-registry.md`

The Backend Constitution is the primary rule by default. An accepted ADR may
authorize only a specific, explicitly described and limited exception; it
cannot silently override the Constitution as a whole. The exception must state
its scope, reason and consequences.

Do not rewrite or reinterpret these documents unless the user explicitly asks
for an approved documentation change.

## Mandatory behavior

- Work only on the requested issue and approved milestone.
- Make one small, coherent and reversible change at a time.
- Read the affected code and tests before editing.
- Preserve business behavior and public API unless explicitly approved.
- Do not introduce a technology, package, layer or abstraction without a
  demonstrated project need.
- Do not add forbidden project dependencies.
- Do not use `AppDbContext` in controllers or put business logic there.
- Obtain identity through `ICurrentUser`; never trust client `currentUserId`.
- Perform resource authorization for every route/query/body identifier.
- Keep `AppRole` separate from team roles.
- Return DTOs; never expose or bind EF entities at the API boundary.
- Pass `CancellationToken` through all asynchronous request paths.
- Use `TimeProvider` for current time in testable business logic.
- Keep external IO outside database transactions.
- Do not use request-scoped `Task.Run`, `.Result`, `.Wait()` or synchronous
  network/file IO.
- Do not log secrets, raw tokens, credentials, full push endpoints or excess
  personal data.
- Do not change schema, routes, payloads, enum serialization or nullable
  semantics as incidental cleanup.
- Never revert unrelated user changes in a dirty worktree.

## Required workflow

1. Confirm the issue, milestone, scope and non-goals.
2. Use a separate branch for every milestone implementation or individual
   implementation task. Codex must not create or switch branches without an
   explicit user command. If the user has already prepared the branch, continue
   in it. A documentation review that makes no changes does not require a new
   branch.
3. Implement the smallest completed slice.
4. Run build after implementation changes.
5. Run targeted tests and the complete required test suite.
6. Run `git diff --check` and inspect the complete diff.
7. Self-review against `docs/governance/code-review-checklist.md`.
8. Perform and report the issue's manual smoke checks.
9. Update Tech Debt or ADR only under their documented rules, and record
   milestone evidence in the corresponding GitHub tracking issue.
10. Stop when the small task is complete; do not absorb adjacent refactoring.

Required backend verification:

```powershell
dotnet restore HockeyPlanner.Backend.sln
dotnet build HockeyPlanner.Backend.sln --no-restore
dotnet test HockeyPlanner.Backend.sln --no-build
git diff --check
```

If a command cannot run, report the exact reason. Never claim verification that
was not performed.

## Documentation decisions

- New confirmed deferred work goes to the Tech Debt Registry, not a new
  milestone.
- Cross-cutting decisions use the ADR template and require user approval.
- DoD criteria remain static during implementation. CI links, manual checks and
  completion evidence belong in the corresponding GitHub milestone tracking
  issue.
- The roadmap order and milestone count are fixed.

## Completion report

Every completed task reports:

- what changed and why;
- files changed;
- build and test results;
- manual checks performed or not performed;
- API, database and configuration impact;
- ADR, DoD and Tech Debt impact;
- remaining risk and rollback method.
