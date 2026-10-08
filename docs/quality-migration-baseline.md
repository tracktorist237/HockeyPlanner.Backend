# Historical migration baseline (HP-78)

## Restoration, not a new initial migration

Restore only these migrations and their Designer files from
`764671ec43a4c130a550f4538969f4b8f78be622`:

- `20260125121252_InitialCreate`
- `20260125125623_Attendance_RenameFieldToUser`
- `20260125133940_Line_RenameFieldToPlayers`

They were removed by `a156f368685c4ce08ac043f8ef1718c13fd81343`.
The original IDs and logic are retained. The current ModelSnapshot is NOT
replaced. No migration history is rewritten and no production connection is used.

## Mandatory regression gate

`MigrationReadinessTests` creates brand-new databases on the validated local
PostgreSQL Testcontainer. It never uses EnsureCreated for those databases.
It covers:

- Empty database -> all 43 migrations through the post-M6 baseline; actual
  PostgreSQL column names/types/nullability match the current model, followed
  by event/attendance/notification/job read-write smoke.
- Existing first-three-migration installation with a user, event, attendance
  and roster -> latest; logs prove only pending migrations ran and data survives.
- Actual pre-M6 schema with historical notification -> latest; only the three
  M6 migrations run and the old notification remains without a fabricated job.
- Post-M6 staging-shaped history -> repeated migration is a no-op; durable work
  and all history rows survive. The frozen reference contains the historical
  IDs confirmed present by the operator and the known post-M6 chain. It is a
  representative fixture, not a live production/staging export.
- Fresh SQL script and idempotent SQL script execute against empty databases;
  idempotent replay on an existing populated database preserves data.

CI runs this suite explicitly before the full suite. The common WebApplicationFactory
still deliberately uses EnsureCreated for fast current-model behavior tests;
those tests are not evidence of migration readiness. No test is skipped.

Generate review artifacts locally (never execute these on production here):

```powershell
dotnet ef migrations script --project HockeyPlanner.Backend.Infrastructure --startup-project HockeyPlanner.Backend.WebAPI --output TestResults/migrations/full-fresh.sql
dotnet ef migrations script --idempotent --project HockeyPlanner.Backend.Infrastructure --startup-project HockeyPlanner.Backend.WebAPI --output TestResults/migrations/full-idempotent.sql
dotnet ef migrations has-pending-model-changes --project HockeyPlanner.Backend.Infrastructure --startup-project HockeyPlanner.Backend.WebAPI
```

The artifacts are ignored build/test output. Historical operations in the
idempotent script are guarded by their exact IDs in `__EFMigrationsHistory`.

## REQUIRED production pre-release check

HP-84 appends `20261008232317_EnforceEventRosterUniqueness` after the 43-migration
post-M6 baseline. Gates now verify the complete current chain rather than using
43 or three pending M6 migrations as the final schema count. The historical IDs
and frozen post-M6 fixture remain unchanged. HP-84 adds valid user/guest roster
backfill, actual composite FK/index checks, fail-closed legacy-anomaly tests,
writer-lock coordination and data-preserving Up/Down/Up coverage. See the separate
[HP-84 pre-merge staging and future production preflight](quality-hp84-migration-preflight.md).
Missing operator preflight blocks adoption even with green local/PR tests.

Before approving any production release containing this restoration, an
authorized operator must run this read-only query against the intended
production database. This task does NOT perform that access.

```sql
WITH required(id) AS (
    VALUES ('20260125121252_InitialCreate'),
           ('20260125125623_Attendance_RenameFieldToUser'),
           ('20260125133940_Line_RenameFieldToPlayers')
)
SELECT r.id AS migration_id,
       EXISTS (SELECT 1 FROM "__EFMigrationsHistory" h
               WHERE h."MigrationId" = r.id) AS already_applied
FROM required r
ORDER BY r.id;
```

ALL THREE rows must report true. Then EF will skip these restored migrations.
If any ID is missing, or the query/history table cannot be read: STOP the
release before application startup/automatic migration. This is a separate
migration-adoption case requiring schema/history reconciliation and approval.
Do not insert fabricated history rows or replay CreateTable against an existing
schema. Do not restore the old snapshot. Normal backup and release safeguards
still apply; presence of these IDs is not a general schema-health guarantee.
