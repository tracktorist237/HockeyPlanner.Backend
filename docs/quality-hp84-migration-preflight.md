# HP-84 operator migration preflight

This is a **pre-merge gate**, separate from implementation, PR CI and independent
review. Until the staging report below is confirmed PASS, the PR stays Draft:
no Ready transition, merge or staging rollout. Sergey confirmed on 2026-10-09
that no HP-84 read-only mechanism or prior operator report is available yet.
Codex does not connect to staging/production or receive new credentials.

## Exact read-only SQL

Use the checked-in, PostgreSQL-tested file:
[hp84-roster-preflight.sql](../tests/HockeyPlanner.Backend.IntegrationTests/Fixtures/Migrations/hp84-roster-preflight.sql).
Use its exact contents from the reviewed PR head; do not replace the query with
an ad hoc deduplication or migration command.

The script starts `BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY`,
sets a transaction-local 30-second statement timeout, executes one SELECT with
CTEs, and ends with ROLLBACK. PostgreSQL enforces read-only execution. No INSERT,
UPDATE, DELETE, DDL, migration/history write, privileged wrapper installation or
external side effect is present. SET LOCAL changes only this transaction's
timeout. The transaction gives the report one consistent snapshot.

It checks user/guest duplicate identities across all lines of an event, missing
player-line-event relationships, and foreign/missing guest relationships. IDs
are used internally for joins/grouping; the output contains **only counts and
status**, without names, notes, User/Event/Guest/Player/Line identifiers or tokens.

## Authorized operator procedure

1. Use the already authorized administrative access and a PostgreSQL SQL client
   to the **intended staging database**. Verify the environment and target locally
   without copying credentials, connection strings or database contents into a
   PR, chat, logs or artifact. Do not give Codex any credentials.
2. Record UTC check time, authorized-operator attestation, reviewed PR head SHA and target label
   `staging` (no host/credential details). Verify the currently deployed application
   revision and existing migration history/schema match the intended upgrade
   baseline. Current post-M6 reference history is in
   [staging-post-m6-history.txt](../tests/HockeyPlanner.Backend.IntegrationTests/Fixtures/Migrations/staging-post-m6-history.txt).
   Unexpected history, missing tables/columns/FKs, missing restored HP-78 IDs or
   schema/history disagreement require operator reconciliation before proceeding;
   never insert fabricated history rows. Record compatibility PASS or BLOCKED.
3. Execute the exact SQL file in a separate SQL session. The file supplies its own
   read-only transaction; do not run it inside an unrelated write transaction.
   Consume the final report and ROLLBACK. On error/timeout/inability to verify the
   target, close the session and record BLOCKED. Do not substitute zero counts.
4. Share only the aggregate report and verification metadata below. A non-empty
   report requires separate human analysis and a business-correct reversible
   reconciliation with a verified backup. This script performs no cleanup and
   grants no authorization to delete a particular roster row.
5. Require compatibility PASS, an all-zero report and independent review/green
   CI for the exact head before a human may decide Ready/merge. Recheck before
   adoption if intervening writers or revision/schema changes invalidate the
   evidence. The migration independently rechecks anomalies under table locks;
   preflight does not eliminate this requirement.

Existing HP-75 restricted SSH wrappers stay unchanged. Do not expand sudo/SSH
permissions, add secrets, use deployment credentials in Codex, or dispatch a
deploy workflow to obtain this report. Use the operator's existing authorized
administrative access for this separate check.

## Aggregate report and acceptance

| Field | Meaning | PASS value |
|---|---|---|
| schema_version | Report format | 1 |
| duplicate_user_groups | Duplicate (event,user) groups | 0 |
| duplicate_user_roster_rows | All roster rows in those groups | 0 |
| duplicate_guest_groups | Duplicate (event,guest) groups | 0 |
| duplicate_guest_roster_rows | All roster rows in those groups | 0 |
| players_missing_line_or_event | Players without a valid parent line/event | 0 |
| players_foreign_or_missing_guest | Players with a missing or wrong-event guest | 0 |
| anomaly_count | Sum of duplicate groups and relationship anomaly counts | 0 |
| status | Query conclusion | PASS |

`anomaly_count` counts category findings, not distinct people or affected rows;
categories may overlap. Do not interpret it as a deletion count.

Example safe evidence (operator completes metadata):

```text
Environment: staging
UTC checked at: <timestamp>
Reviewed PR head: <full SHA>
Authorized-operator attestation: confirmed or pending
Schema/history/application compatibility: PASS or BLOCKED
schema_version=1
duplicate_user_groups=0; duplicate_user_roster_rows=0
duplicate_guest_groups=0; duplicate_guest_roster_rows=0
players_missing_line_or_event=0; players_foreign_or_missing_guest=0
anomaly_count=0; status=PASS
Read-only transaction completed and rolled back: yes
```

PASS requires all evidence present, all counters zero, correct report version,
correct target and compatibility PASS. Any anomaly, missing report, unavailable
mechanism, failed query or unverified compatibility means **BLOCKED**. Keep the
Draft PR and do not merge/roll out. Do not convert failure into a warning.

## Migration, rollback and future production release

The appended `20261008232317_EnforceEventRosterUniqueness` migration locks events,
lines, event_guests and players in its transaction, fails closed on anomalies,
backfills Player.EventId from Line.EventId and installs composite FKs/unique
indexes. It preserves all existing IDs and field values. Historical migrations
and `__EFMigrationsHistory` are not rewritten. Locks can temporarily block writers;
coordinate adoption with the new binary, because old writers do not supply EventId.

Before adoption, confirm a verified backup and a coordinated application/schema
rollback plan. Local Up -> Down -> Up tests preserve every legacy roster/guest/
attendance field. Reverse the new migration before starting an older binary;
application-only rollback against the new required column is unsafe. This task
does not perform any deployment or live rollback.

Every future production release containing HP-84 requires a **separate** authorized
read-only production execution of this aggregate query and an operator check of
migration history, actual schema and intended binary compatibility. Also satisfy
the existing [HP-78 production restoration gate](quality-migration-baseline.md).
Staging evidence cannot replace a production release check. If production already
has HP-84, verify its composite FKs/indexes and history against the reviewed model;
the same SELECT can still report aggregate data anomalies. Missing evidence or
incompatibility blocks the release. No production connection, change or release
is authorized to Codex by this document.

## PostgreSQL proof

`MigrationReadinessTests.Hp84.cs` executes this exact SQL against isolated databases:
valid legacy data returns PASS/zero; duplicate users, duplicate guests, foreign
guests and broken event relationships return BLOCKED. Full row JSON and migration
history are compared before/after preflight, proving no persistent mutation.
Migration failure preserves data/history/schema; the table-lock test proves a
legacy writer's newly committed anomaly is rechecked before adoption.
