# HP-83 author handoff: team tables and event protocols (M7)

Backend base: `0e2bbb06a7630c52e45be01f221cbedb87700c46`.
Frontend base: `d9fb43fe8099e2208e7c06b4a7e9f720359d3b57`.
Paired branch: `quality/hp-83-team-tables-event-protocol-use-cases`.
Exact final heads and paired Draft PR/CI links are recorded in the PR bodies.
This is author evidence, not independent approval. [HP-83](https://linear.app/hockeyplanner/issue/HP-83/m7-secure-and-extract-team-tables-and-event-protocol-use-cases) remains In Progress.

## Authentication and resource access

All eight TeamTablesController routes require JWT bearer authentication. Actor
identity comes exclusively from ICurrentUser. No action binds currentUserId;
unknown legacy query values, including malformed values, are ignored. There is
no query/JWT equality check or query fallback.

| Actor | Team tables / event protocols | Create / bulk update / row update |
|---|---|---|
| Anonymous | 401 | 401 |
| Persisted Member of the team | 200; CanManage=false | 403 |
| Persisted Admin or Owner | 200; CanManage=true | Allowed |
| Authenticated foreign user | 403 for existing authorized resource graph | 403 |

The feed contains only JWT actor memberships and derives each management flag
from CoreTeamRolePolicy. Public team visibility does not grant table access.
Missing teams/events/tables/protocols/rows produce established 404 semantics;
substituted identifiers that do not form the requested graph produce 404.
Team/table and event/table ownership, event/protocol association, and every bulk
or single row's protocol membership are checked before mutations. Duplicate bulk
row IDs produce 400 through the existing ProblemDetails exception handler.
Foreign bulk rows are rejected as 404, including a mixed valid/foreign request,
instead of silently ignoring the substituted row.

## Application boundary and compatibility

TeamTablesController contains only route/body binding, ICurrentUser retrieval,
CancellationToken forwarding, service calls and Ok result mapping. It contains
no EF, DbContext, role decisions, mapping, synchronization or recalculation.
One scoped ITeamTablesService/TeamTablesService owns this coherent slice,
registered by Application DependencyInjection. The unchanged model definitions
move from WebAPI to Shared.Models.Tables so Application does not reference WebAPI.
The existing transitional Application-to-Infrastructure dependency is unchanged.

Persisted membership establishes view access; CoreTeamRolePolicy.CanManage
centralizes Owner/Admin decisions. All asynchronous EF execution and transaction
operations receive the request CancellationToken. TimeProvider.GetUtcNow().UtcDateTime
replaces moved DateTime.UtcNow calls, retaining UTC DateTime wire/storage semantics.
Protocol creation and each update own an explicit transaction covering protocol
persistence and aggregate recalculation, including row synchronization on creation.
There is no external IO or nested service transaction.

DTO IDs, team/event/table names, timestamps, numeric template, row counts,
CanManage, jersey projection/fallback, photos and row fields are unchanged.
Table sorting remains Points descending, Games ascending, Goals descending,
LastName then FirstName. Protocol rows remain LastName then FirstName.
PlayerStats is the only accepted creation template; name whitespace normalization,
120-character limit and default Статистика игроков are preserved. Existing team
members seed table rows. Confirmed attendance initializes Games=1, other rows=0.
Stats clamp to 0..999, Points=Goals+Assists, and all protocols contribute to the
TeamTableRow aggregate. CreatedByUserId is now the JWT actor for both resources.

## Corrected HP-79 pre-error writes

Foreign table GET previously synchronized Team A's memberships into Table B
before discovering the team/table mismatch. HP-83 validates ownership first.
The regression signs Team A's legitimate Owner JWT, proves Table B has three
rows and no A user, receives 404, then compares every row's identity, stats and
timestamps in a fresh DbContext. Rows remain exactly unchanged; A is absent.

Duplicate protocol POST previously synchronized a missing table row before
returning 409. HP-83 checks duplicate template before synchronization. The
regression adds a legitimate B Member after table seeding, proves the row absent,
posts with B's signed Owner JWT and receives 409. A fresh scope proves the row
still absent, table row count still three, protocol count still one, and existing
protocol identity/creator/timestamps/all row fields unchanged. The HP-79 tests
that expected spoofing and both unsafe writes are retired or corrected.

## Frontend companion

getTablesFeed, getTeamTables, getTeamTable, createTeamTable,
getEventTableProtocols, createEventTableProtocol, updateEventTableProtocol and
updateEventTableProtocolRow take only resource IDs and bodies. Every route uses
the existing authFetch-backed team transport, preserving credentials, ten-second
timeout, encoded resource IDs, methods, JSON bodies and TeamsApiError parsing.
requireCurrentUserId is removed; there is no cached-user identity fallback.
TeamTablesPanel/EventTableProtocolsPanel and their three parents no longer pass
or gate this API slice on a selected actor ID. Panel tests cover reads, creation
and bulk save independently of cached identity.

## Contracts

No canonical fixture change is required: canonical consumers do not yet contain
table/protocol DTO captures, existing entries are unaffected, and HP-83 has real
HTTP DTO/error/persistence assertions. ConsumerContractTests and byte comparison
pass with zero drift. Neither generated fixture was edited. Backend and frontend
canonical SHA-256:
`4bf24556d8855d2e09dc853ff9022389faf312807d73dc0432f9fc153d63fe06`.
Paired task branches must both be pushed before PR validation so companion
revision selection resolves HP-83 rather than develop.

## Local backend evidence

- dotnet restore HockeyPlanner.Backend.sln: passed.
- dotnet build HockeyPlanner.Backend.sln --no-restore: passed, 0 errors.
- Focused TeamTablesAuthorizationTests + TeamMediaAndTablesBaselineTests +
  ConsumerContractTests: 32 passed, 0 failed/skipped (includes both side effects).
- python -m unittest discover -s scripts/quality -p "test_*.py": 34 passed,
  including 3 new formatting-independent controller/service boundary guards.
- MigrationReadinessTests: 5 passed, 0 failed/skipped, real PostgreSQL migrations.
- Full PostgreSQL suite: 667 passed, 0 failed/skipped.
- check_test_results.py TestResults/hp83-full.trx: 667 passed, zero failed/skipped.
- dotnet build HockeyPlanner.Backend.sln -c Release --no-restore: passed, 0 errors.
- git diff --check: passed; no fixture drift.

The initial baseline build passed; baseline tests failed because Docker was
stopped (84 passed, 566 fixture failures, no skipped tests). Starting the local
Docker daemon restored the isolated suite. One intermediate explicit pipe URI
was rejected; default local Docker discovery works. Final counts above are the
successful corrected runs, not the failed baseline/intermediate attempts.
Build/restore retain 21 existing NuGet advisory warnings (NU1901/NU1902/NU1903),
including transitive SSH.NET, Microsoft.OpenApi and NuGet libraries. No package
or advisory baseline was changed. Remote audits remain a separate mandatory gate.

## Debt, risks and rollback

SEC-001 is Resolved only for its registered M7 team-operation scope: searches of
TeamsController, TeamTablesController, their application services and frontend
teams.ts find no client actor query. Remaining Exercises/Goalies actor paths are
outside M7 and are not claimed fixed. ARC-002 stays Open for other controllers;
HP-83 records the TeamTables extraction. ARC-004 stays Open for its broader
multi-feature controller definition. TECH-002, TECH-003, PERF-001, INF-006 and all
unrelated debt remain unchanged. No Constitution, roadmap, DoD or ADR is changed.

Successful table-detail GET retains the existing membership-row synchronization
behavior; only rejected resource substitutions are made side-effect free.
Concurrent template creation remains subject to the existing application-level
duplicate check; this issue adds no schema constraint or new concurrency policy.
Manual staging/production/device verification is not performed in this author
run. The isolated browser gate and exact-head PR CI belong to paired PR evidence.
Independent review is pending a separate session; human merge is required.

Scope excludes master, VERSION, migration/schema, packages, workflows, production
config/data, deployment, release/tagging, GitHub settings, Linear state and M8/M9.
Rollback: revert HP-83 commits in both repositories. No DB/config rollback.
