# ADR-014: Event roster uniqueness and atomic attendance consistency

- Status: Accepted
- Date: 2026-10-09
- Milestone: M7
- Owners: HockeyPlanner maintainers
- Related issues/debt: [HP-84](https://linear.app/hockeyplanner/issue/HP-84/m7-enforce-event-level-roster-uniqueness-and-atomic-attendance), TECH-002, TECH-003

## Context

At backend develop `662df5e4c03902df821385c50c424072a985c0a5`, PlayerConfiguration
protects `(LineId, UserId)` and `(LineId, EventGuestId)`, permitting the same
identity in multiple lines of one event. Player stores no EventId. Line stores
EventId. EventGuest belongs to one event, but its current single-column player
FK does not enforce that this is the line's event.

EventService.UpdateAttendance loads a tracked Attendance, writes its existing
row immediately with ExecuteUpdateAsync, then removes a tracked Player through
SaveChangesAsync. A failure after the immediate write leaves a partial result.
UpdateEventGuestAttendance already uses one tracked SaveChangesAsync.

LineService.CreateRoster and EventDataTransferService.ReplaceRosterAsync both
create players. Transfer merges guests by normalized first/last name; distinct
source guests can map to one target guest. New constraints must reject this
collision atomically with a controlled error rather than discard a player.

The Development Process requires an ADR when schema compatibility or transaction
semantics are decided. AGENTS.md requires human approval of cross-cutting
decisions. Sergey approved this direction in the implementation session on
2026-10-09, subject to DB enforcement, non-destructive fail-closed migration,
real PostgreSQL fault injection, mandatory staging preflight before merge, and a
separate production-release preflight/compatibility check. Approval authorizes
implementation and Draft PR only; no merge, deployment or issue closure.

## Decision

Implement only the following approved HP-84 slice:

1. Add required Player.EventId. Introduce alternate keys `(Id, EventId)` on Line
   and EventGuest. Replace the player's line FK with `(LineId, EventId)` and its
   guest FK with `(EventGuestId, EventId)`. Retain line-to-player cascade and
   guest-to-player restrict behavior. Retain Line.Event and other unrelated FKs.
   PostgreSQL must reject a forged event/line or event/guest combination.
2. Add filtered unique indexes `(EventId, UserId)` where user_id is not null and
   `(EventId, EventGuestId)` where event_guest_id is not null. Preserve nullable
   identity fields, numeric roles, positions, names, numbers and existing DTOs.
   No extra user/guest identity constraint is introduced incidentally.
3. Populate EventId explicitly from the authorized event in roster creation and
   from the target event in transfer. Update relevant test fixtures accordingly;
   do not trust a caller's EventId as enforcement. Database FKs remain definitive.
4. Validate duplicate identities separately for users and guests across the
   effective request after existing Declined filtering. Use BusinessRuleException
   (400) for malformed duplicate input, following ApiExceptionHandler. Translate
   only the two named roster unique-constraint violations to a sanitized
   ConflictException (409) for persistence races/repeated additions. Apply the
   same narrow handling to transfer collisions. Other database errors retain
   their existing handling. No global DbUpdateException conversion.
5. Keep CreateRoster's single SaveChanges atomic and UpdateRoster's existing
   replacement transaction owner. NotificationService currently delegates to
   NotificationOutbox.EnqueueAsync, which saves durable intent through the same
   AppDbContext without external delivery. Retain this enqueue inside the existing
   replacement transaction and test that its nested save cannot commit separately.
   Do not move it after commit or expand notification durability/retry/outbox scope.
6. Replace UpdateAttendance's immediate existing-row write with assignments to
   the already tracked Attendance. Status, Notes, RespondedAt and UpdatedAt,
   together with any Player removal, are persisted by the one existing
   SaveChangesAsync. EF's PostgreSQL transaction is the atomic boundary owned
   by this application use case. Conflict and authorization checks precede
   mutation. Preserve Confirmed, Pending, Declined and IgnoreConflicts behavior.
   Preserve guest attendance's existing single-save boundary.

### Migration and adoption

Append one ordinary EF migration and generated snapshot/designer. Do not change
historical IDs or migrations. In one transactional migration, check for legacy
event-level user/guest duplicates, players without a valid line/event, and
players referencing a guest from another event. Fail closed with a fixed safe
message before schema/backfill changes if any exist. Never auto-deduplicate.

For valid rows, backfill Player.EventId from Line.EventId, preserve all IDs and
other columns, require non-null, then install keys/FKs/indexes. Concurrent writes
during adoption must be excluded by migration table locks, not only preflight;
test the locking/backfill semantics and migration rollback on PostgreSQL.

A read-only operator preflight against the intended staging database is a
**pre-merge blocker**. An unavailable preflight is also a blocker: no merge or
staging rollout. This task performs no staging/production access. The
operator must confirm the anomaly report is empty and record the evidence. If
anomalies exist, human selection of business-correct reconciliation and backup
is required before merge; retain the PR as Draft. A clean local test fixture
cannot establish cleanliness of any deployed database.

Every future production release containing this change additionally requires a
separate authorized read-only production preflight and verification of migration
history/schema compatibility with the intended binary. Staging evidence cannot
replace this release-specific gate. Missing evidence or anomalies block release;
no history fabrication, automatic deduplication or production writes are authorized.

## Alternatives considered

- Application duplicate validation alone: cannot protect overlapping contexts
  or direct SQL; rejected.
- Serialize service writes by event-row locks only: existing direct persistence
  paths and SQL can bypass the lock protocol; insufficient database invariant.
- PostgreSQL triggers with event advisory locking: no denormalized field, but
  correctness requires handling snapshot visibility, line/guest moves, lock
  ordering and writes outside EF. More bespoke infrastructure and migration
  behavior than declarative indexes/composite FKs; not selected.
- Separate event-membership table: duplicates Player identity storage and adds
  lifecycle orchestration; unnecessary for the current model.
- Explicit transaction around ExecuteUpdate plus SaveChanges: atomic in the
  database, but retains stale tracked Attendance and an additional transaction
  boundary. One tracked save matches the existing guest path and is smaller.

## Consequences

Users remain independently rosterable in different events; guests are restricted
to their own event. PostgreSQL unique indexes arbitrate overlapping transactions
and composite FKs prevent event spoofing. Public payloads need no EventId field
on player DTOs. Existing invalid data requires explicit operator reconciliation.
Old application binaries do not supply the required new column: coordinated
schema/application adoption and rollback are required. No rolling mixed-version
compatibility or production compatibility is claimed.

## Verification

Implementation must supply real PostgreSQL tests, categorized HP84, for duplicate
user/guest within/across lines, independent contexts and overlapping transactions,
same user across events, mixed identities, foreign guests, replacement/transfer
rollback, and unchanged existing roster after rejection. Coordinate concurrent
tests with barriers and observe the loser waiting on the first transaction;
inspect final persisted state from a fresh context. No sleeps, retries or InMemory.

Failure injection must observe a successfully executed Attendance SQL update
inside the save transaction, then throw or cancel before Player deletion. Disable
command batching in that test to expose the actual boundary. Verify both original
rows, notes and timestamps from a fresh context; verify ordinary success paths,
guest rollback, conflict confirmation and JWT/foreign-resource denial as well.

Migration tests must cover the current complete chain, pre-M6/post-M6 upgrade,
preserved valid historical players/attendance, anomalous legacy data causing a
transactional non-destructive failure, fresh/idempotent scripts, second Migrate
and actual model/FK/index consistency. Preserve the frozen HP-78 baseline;
remove obsolete assumptions that exactly three migrations remain after pre-M6.
Run the full required Debug/Release, integration, contract, Python and PR CI gates.
These are required future checks, not results claimed by this proposal.

## Rollback or supersession

Before merge/adoption, retain a verified backup and record preflight. For an
isolated rollback rehearsal, reverse only the new migration before starting the
previous application, restoring original line-scoped indexes and FKs, then drop
Player.EventId and added alternate keys. Preserve roster rows and IDs. Runtime
rollback without schema coordination is unsafe because the previous writer does
not supply EventId. No task execution against staging or production is authorized.
Changing public payloads, silently cleaning duplicates, or widening notification
semantics requires a separate approved decision.
