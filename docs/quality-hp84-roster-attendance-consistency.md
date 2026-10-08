# HP-84 implementation handoff

Implementation complete; independent review pending. **PR Ready/merge/staging
rollout BLOCKED until operator staging preflight PASS**. The missing preflight
was confirmed by Sergey and does not block implementation, Draft PR or review.
This author session supplies no independent approval and performs no merge/deploy.

- Issue: [HP-84](https://linear.app/hockeyplanner/issue/HP-84/m7-enforce-event-level-roster-uniqueness-and-atomic-attendance).
- Milestone: M7 — Teams Controllers.
- Backend base: `662df5e4c03902df821385c50c424072a985c0a5` (fresh origin/develop).
- Branch: `quality/hp-84-roster-attendance-consistency`.
- Exact final head, Draft PR and CI run links are recorded in the PR body and
  final session handoff. Resolve the branch's current full SHA before review;
  any later commit requires new CI/review. No self-referential commit hash is
  embedded in this versioned document.
- Frontend inspected read-only: `5489c44eeb20bbfe3e6778ce524293887250178a`.
- Both worktrees were initially clean; prepared HP-83 history was preserved.
  Linear HP-84 remains Backlog, its blockedBy HP-79 is Done. No issue closure.
- No GitHub M7 tracking issue was available (repository issue listing contains
  only M2 #1); milestone evidence is provided in this handoff and Draft PR.

## Approved design and scope

[ADR-014](architecture/adr/ADR-014-event-roster-attendance-consistency.md) was
approved by Sergey on 2026-10-09 with DB-enforced consistency, no automatic
cleanup, PostgreSQL rollback proof, staging pre-merge preflight, and a separate
future production-release preflight/compatibility check. It is Accepted.

TECH-002: Player.EventId is required. PostgreSQL unique filtered indexes
`ux_players_event_user` and `ux_players_event_guest` enforce one roster row per
(event,user) / (event,guest), including different lines and concurrent writers.
Composite FKs `(LineId, EventId) -> Line(Id, EventId)` and
`(EventGuestId, EventId) -> EventGuest(Id, EventId)` prevent event spoofing.
Line's existing event FK connects this invariant to Events. Line-to-player
cascade and guest-to-player restrict remain effective. No extra identity check,
position restriction, DTO field, route or numeric enum change was introduced.

CreateRoster populates EventId from its authorized event and validates separate
user/guest identity spaces after existing Declined filtering. Input duplicates
return BusinessRuleException/400. Only the two named PostgreSQL unique violations
are converted into ConflictException/409, without SQL details. Failed duplicate
additions are detached from the tracker. UpdateRoster keeps its replacement
transaction and existing durable outbox enqueue before commit; nested saves do
not independently commit. Global notification durability/retry is unchanged.

EventDataTransferService is the other Player creation path. Copies use target
EventId. A guest-name merge that collapses distinct source guests onto one target
guest is rejected by the same DB invariant, translated to safe 409, with the
entire transfer rolled back. No arbitrary player is dropped. Existing transfer
modes/source-target authorization remain unchanged.

TECH-003: UpdateAttendance now assigns Status, Notes, RespondedAt and UpdatedAt
to the already tracked Attendance. This use case owns the single SaveChanges
boundary for answer and required Player removal; EF/PostgreSQL supplies the
transaction for the multiple SQL commands. Authorization and HP-70 conflicts
precede mutation. Failed tracked answer/removal intent is restored or detached
so later SaveChanges on a reused context cannot silently commit it. Guest
attendance retains its existing one-save boundary. Existing timestamps and
Confirmed/Pending/Declined behavior are preserved.

## PostgreSQL proof

`Hp84RosterAttendanceTests` and HP84 methods in `MigrationReadinessTests.Hp84.cs`
are categorized `[Trait("Category", "HP84")]`.

- Same-line and cross-line duplicate User/Guest rejection, complete HTTP 400/409
  ProblemDetails aliases, and retained prior roster/lines after rejection.
- Two independent contexts/connections with explicit overlapping PostgreSQL
  transactions. The winner saves but remains uncommitted; `pg_blocking_pids`
  proves the second writer actually waits for it. Winner commit causes unique
  rejection of the loser. A fresh context proves one identity row, intact winner
  and prior roster, and no losing partial line. No sleeps or retrying writes.
- Same user across events, distinct guests, and a User/Guest sharing one UUID
  are allowed in both database and request validation.
- Foreign guest / forged event-line inserts, direct player/line/guest updates,
  actual composite FK rejection, line cascade and guest restrict.
- Declined duplicates are removed before validation; foreign-guest replacement
  and denied JWT/spoofed actor/member mutations preserve persisted state.
- Attendance existing/new success for all statuses, no-player/repeated paths,
  guest success, HP-70 warning-before-mutation and reused-context override.
- A command interceptor with MaxBatchSize(1) observes successful SQL answer
  UPDATE, reads the new status on the same PostgreSQL transaction, and injects
  failure/cancellation before any Player DELETE. Fresh contexts prove rollback
  of answer status/notes/timestamps and cleanup. New-answer INSERT failure is
  also atomic. No InMemory/mock persistence is used as proof.
- Replacement failure after actual nested notification_jobs INSERT preserves
  original roster and rolls back both notification and job.
- Actual HTTP guest-merge transfer collision returns 409 and preserves target
  roster, source event/guests and target description/guest set.

## Migration and operator safety

One appended migration: `20261008232317_EnforceEventRosterUniqueness`, generated
Designer and updated ModelSnapshot. Historical IDs/implementations and frozen
HP-78 fixture are untouched. No EnsureCreated substitutes for migration evidence.

In a transaction it locks events/lines/event_guests/players, checks duplicates
and broken parent/guest relationships, fails closed with a fixed message, adds
nullable event_id, backfills it from Line.EventId, requires non-null and installs
composite keys/FKs/indexes. No default event value, deletion or automatic dedup.

Real migration tests cover the whole fresh chain, early/pre-M6/post-M6 upgrades,
fresh/idempotent scripts/replay and exact legacy row JSON preservation. Up ->
Down -> Up preserves all old user/guest roster/attendance fields. Legacy user/
guest duplicates, foreign guest and missing event cause no schema/history/data
change, including repeated failure. A held legacy writer blocks migration table
locks; its newly committed duplicate is then rejected by the locked preflight.
Existing MigrationReadinessTests derive the latest chain rather than assuming
three pending M6 migrations or equating a frozen baseline with the latest schema.

[Operator procedure](quality-hp84-migration-preflight.md) links the exact tested
SQL. It runs READ ONLY/REPEATABLE READ, SELECT-only CTEs and ROLLBACK, returning
only aggregate counts/status without IDs or personal data. Tests compare data
and history before/after this exact SQL. The operator must confirm zero counts,
correct target and schema/history/binary compatibility; absent or failed evidence
means BLOCKED. Existing SSH wrappers, permissions and secrets remain untouched.
No staging/production connection was made. Future production release needs its
own separate preflight plus HP-78 restoration/schema compatibility evidence.

Rollback requires backup and coordinated schema/application handling. Reverse
only the new migration before starting an old binary, which does not supply the
new required EventId. App-only rollback is unsafe. Locks may block writers during
adoption; no live deployment/rollback or production compatibility is certified.

## Verification

| Check | Result | Evidence / limit |
|---|---|---|
| Backend/frontend fetch and clean-start checks | PASS | Current SHAs above; frontend unchanged |
| dotnet restore HockeyPlanner.Backend.sln | PASS | Existing NuGet warnings |
| dotnet build HockeyPlanner.Backend.sln --no-restore | PASS | 0 errors, 21 warnings |
| dotnet test HockeyPlanner.Backend.sln --no-build --filter Category=HP84 | PASS | 57 passed, 0 failed/skipped |
| Explicit MigrationReadinessTests filter | PASS | 11 passed, 0 failed/skipped |
| Full PostgreSQL/HTTP suite | PASS | 724 passed, 0 failed/skipped; final verification recorded in PR |
| Release build -c Release --no-restore | PASS | 0 errors, 21 warnings |
| Python quality unittest discovery | PASS | 34 tests |
| Backend/frontend serialized contract comparison | PASS | Frontend origin/develop fixture; no fixture edits |
| EF migrations has-pending-model-changes | PASS | No pending changes; --no-build uses verified Debug build |
| Fresh/idempotent SQL generation | PASS | Ignored TestResults/migrations/full-{fresh,idempotent}.sql |
| git diff --check / full author self-review | PASS | Required scope only; independent review still pending |
| Remote mandatory PR CI | PENDING | Exact-head result/URL supplied in PR and final session handoff |
| Live staging preflight | BLOCKED | Operator mechanism/report unavailable; PR must stay Draft |
| Staging/production deployment or smoke | NOT RUN | No deployment authorized/performed |
| Frontend Jest/Playwright | NOT RUN | No frontend edit; backend HTTP suite and contract comparison passed |

Final local test TRX files are ignored test output, not committed artifacts.
Existing NuGet warnings concern System.Security.Cryptography.Xml, Microsoft.OpenApi,
NuGet.Packaging, NuGet.Protocol and SSH.NET. They were already present at baseline;
this task changes no dependencies and does not claim those advisories resolved.

## Complete changed-file scope

```text
HockeyPlanner.Backend.Application/Implementations/Services/EventService.cs
HockeyPlanner.Backend.Application/Implementations/Services/LineService.cs
HockeyPlanner.Backend.Core/Entities/Player.cs
HockeyPlanner.Backend.Infrastructure/Data/Configurations/EventGuestConfiguration.cs
HockeyPlanner.Backend.Infrastructure/Data/Configurations/LineConfiguration.cs
HockeyPlanner.Backend.Infrastructure/Data/Configurations/PlayerConfiguration.cs
HockeyPlanner.Backend.Infrastructure/Data/Migrations/20261008232317_EnforceEventRosterUniqueness.cs
HockeyPlanner.Backend.Infrastructure/Data/Migrations/20261008232317_EnforceEventRosterUniqueness.Designer.cs
HockeyPlanner.Backend.Infrastructure/Data/Migrations/AppDbContextModelSnapshot.cs
HockeyPlanner.Backend.Infrastructure/Data/RosterConstraints.cs
HockeyPlanner.Backend.WebAPI/Services/EventDataTransferService.cs
docs/architecture/adr/ADR-014-event-roster-attendance-consistency.md
docs/governance/tech-debt-registry.md
docs/quality-hp84-migration-preflight.md
docs/quality-hp84-roster-attendance-consistency.md
docs/quality-migration-baseline.md
tests/HockeyPlanner.Backend.IntegrationTests/Fixtures/Migrations/hp84-roster-preflight.sql
tests/HockeyPlanner.Backend.IntegrationTests/Fixtures/TwoTeamSecurityScenarioBuilder.cs
tests/HockeyPlanner.Backend.IntegrationTests/HockeyPlanner.Backend.IntegrationTests.csproj
tests/HockeyPlanner.Backend.IntegrationTests/Security/RosterAndPlayerAuthorizationBaselineTests.cs
tests/HockeyPlanner.Backend.IntegrationTests/Services/EventDataTransferServiceTests.cs
tests/HockeyPlanner.Backend.IntegrationTests/Services/Hp84RosterAttendanceTests.cs
tests/HockeyPlanner.Backend.IntegrationTests/Services/MigrationReadinessTests.cs
tests/HockeyPlanner.Backend.IntegrationTests/Services/MigrationReadinessTests.Hp84.cs
```

The existing transfer filtering test now seeds two distinct declined users across
its two lines, preserving every assertion about partial/empty lines and order.
Its old fixture intentionally placed the same declined user in two source lines,
which the approved new DB invariant forbids. No assertion or security gate was
weakened. Composite FK relationship fixup supplies EventId for existing graph
fixtures; fixtures creating independent Player rows set it explicitly.

## Review, debt and remaining risk

TECH-002/TECH-003 are Resolved with concrete code/test evidence. Staging adoption
remains blocked pending operator preflight. ADR-014 is Accepted under explicit
user approval; roadmap/DoD/Constitution/security policy are unchanged. Global
TECH-006, PERF-001, HP-85/HP-86, M8 and M12 remain outside scope.

Independent read-only review must verify the exact final base/head, complete diff,
production paths, PostgreSQL tests, migration/preflight/rollback, notification
transaction boundary, auth/HTTP contracts and CI. Author self-review is not
approval. No GitHub APPROVE was sent; PR stays Draft and a human decides merge
only after all gates pass. Any new commit invalidates earlier review/CI evidence.

No master, VERSION, production DB/config, release/tag, direct develop push,
force-push, deploy, merge, or HP-85/HP-86 work was performed.
