# HP-81 core team use cases and Owner invariant

Implementation base: `645386ce5edfba457ddbaa529325cea7f95fc13c`.
Task branch: `quality/hp-81-core-team-use-cases` (backend and contract companion).
HP-81 remains In Progress. This is author evidence, not independent approval.

## Application boundary

`ICoreTeamService` / scoped `CoreTeamService` owns the 12 core use cases:
GetMyTeams, GetPublicTeams, GetTeam, GetTeamMembers, CreateTeam, UpdateTeam,
UpdateTeamMember, RemoveTeamMember, JoinByCode, JoinPublic, LeaveTeam and
UpdateMyTeamJerseyNumber. JoinTeamInternal is a private application helper.
Each controller action resolves JWT identity through ICurrentUser, calls one
use case with RequestAborted and returns DTO/status. Invalid authenticated
identity remains 401. The core TeamsController slice no longer accesses
DbContext directly or decides team roles.

Application uses its existing Infrastructure reference (ARC-001 deferred to
M12). There is no new project/package dependency, repository abstraction or
framework. All EF async calls receive cancellation; writes use TimeProvider.
The existing exception pipeline maps not-found/authorization/business/conflict
exceptions to 404/401-or-403/400/409.

TeamDto, TeamMemberDto, TeamContactItemDto and the five used core request models
move to Shared/Models/Teams. No entity crosses the service/controller boundary.
News DTOs/requests and the unused JoinTeamRequest stay in WebAPI. Two existing
external-league consumers only change their import of TeamContactItemDto.

## Central role matrix

`CoreTeamRolePolicy` is the single core role boundary, including invite visibility.

| Operation | Owner | Admin | Member / non-member |
|---|---|---|---|
| Manage team / see invite | Yes | Yes | No |
| Edit member badge/number | Any target | Any target, preserving base by explicit user clarification | 403 |
| Change non-owner Member/Admin role | Yes | 403 | 403 |
| Demote Owner / promote another member to Owner | 400 | 403 | 403 |
| Remove Member | Yes | Yes | 403 |
| Remove Admin | Yes | 403 | 403 |
| Remove Owner | 400 | 400 | 403 |
| Leave | 400 in every membership-count state | 204 | Member 204; non-member 404 |

Self-removal through `/members/{userId}` still returns 400 and directs callers
to leave. Missing management targets still return 404 after actor authorization.
Repeating the existing role remains a metadata-only request; it never transfers
ownership. Unsupported role values cannot be used for a role transition.

The user explicitly chose to preserve Admin metadata access to Admin/Owner
targets after the implementation author identified the discrepancy between the
initial matrix and the real base. Dedicated policy and HTTP regressions freeze
that clarification. No frontend product behavior is changed.

## TECH-001 and concurrency

Before: the last Owner could leave with 204, keeping a persisted ownerless Team.
After: Owner leave always raises BusinessRuleException / HTTP 400, retaining the
Team and original Owner membership. The existing owner-with-members error text
is retained. There is no implicit deletion, promotion or ownership transfer.

The invariant is structural within these core API transitions: creation saves
Team and Owner in one SaveChanges transaction; joins only create Member; updates
never assign Owner to another member or change the Owner role; removal and leave
never delete Owner. Concurrent valid non-owner mutations cannot change that.
No schema migration, per-team/global lock or extra transaction is needed.
This does not claim to repair historically ownerless rows or protect arbitrary
out-of-band SQL / unrelated user-deletion cascades.

`CoreTeamOwnerConcurrencyTests` runs real signed JWT HTTP requests against the
isolated PostgreSQL Testcontainer. A test-only DbCommandInterceptor holds each
request after its first real membership SELECT until every participant arrives.
Arrival counts are asserted; the bounded wait fails if setup serializes requests.

- Last Owner: concurrent leave, leave and self-demotion, all 400.
- Owner plus last Member: Owner leave 400 while Member leave succeeds (204).
- Mixed: Owner leave/demotion, distinct Admin removing Owner, attempted Owner
  assignment, Member leave and valid Admin metadata update overlap. Forbidden
  Owner operations return 400; allowed non-owner operations persist.

Every case verifies through a fresh DbContext that the Team exists, exactly the
original Owner user and membership ID remain, no forbidden Owner field mutation
persisted, and the allowed companion mutations have their expected results.

## Compatibility and contract

Existing create/update normalization, contacts, jersey validation/conflicts,
join code normalization/idempotency, role projections and status precedence are
preserved. Existing DTO projections are retained, including the historical join
member-count expression; this extraction does not fix unrelated projection debt.
HP-79/80 security characterization stays active except the intentional last-owner
204-to-400 regression update.

ConsumerContractTests adds only `teamLastOwnerLeave`, captured from real HTTP on
a fixed one-Owner team, and verifies the persisted Team/Owner in a fresh scope.
All prior fixture entries stay unchanged. The generated bundle is copied
byte-for-byte to the frontend same-named branch; its leaveTeam consumer test
checks TeamsApiError status/detail. No hand-authored ProblemDetails.

## Remaining debt and scope

TeamsController still queries DbContext for GetTeamNews, GetNewsFeed,
CreateTeamNews, UpdateTeamNews, DeleteTeamNews, UploadTeamMedia (avatar/cover),
and CanManageTeamAsync (news/media, including news-image upload). Those methods
and their news/media helpers remain HP-82, unchanged. PWA logo is unchanged.
TeamTablesController and event/table protocol authorization remain HP-83.
ARC-002 and ARC-004 stay Open; only the core controller slice is extracted.
SEC-001 retains the merged HP-80 / open HP-83 split. Admin/Goalies remain later
roadmap work. TECH-002/003, PERF-001, HP-87/88 are outside this change.

## Verification and handoff

Focused command: `dotnet test HockeyPlanner.Backend.sln --no-build --filter
"Category=HP81|Category=HP79|FullyQualifiedName~ConsumerContractTests"`.
It includes direct role-policy tests, DI application use-case time/cancellation
tests, HTTP create/update/join/metadata/jersey regressions, existing security
coverage, sequential Owner protection and the three concurrency scenarios.

Run restore, Debug build, MigrationReadinessTests, the full PostgreSQL suite,
check_test_results.py (reject skipped/missing tests), Release build, Python
quality tests and diff checks. Frontend companion requires focused consumer,
full Jest, production build, Python quality tests and byte equality. Final
counts, fixture hash, heads, PR links, warnings and exact CI run IDs belong in
the Draft PR and author handoff; no unexecuted check is implied here.

Independent review is pending a separate session. Human merge only. No deploy,
master/VERSION/production changes, tags or release. Rollback reverts backend
extraction and paired fixture/test together; no database rollback is needed,
but reverting runtime code reintroduces TECH-001 and requires an explicit decision.
