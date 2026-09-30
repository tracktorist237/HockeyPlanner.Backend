# M7 team API baseline (HP-79)

This is a characterization of backend base
`f80940adfb4e51a1dbd6119cfb046d5f837f2b64`, not an approved security policy.
Production code is unchanged. All assertions execute; none are skipped or
expected failures. HP-80/81/82/83 must deliberately update these assertions.

## Evidence and isolation

`TeamApiBaselineTests` and `TeamMediaAndTablesBaselineTests` use real ASP.NET
HTTP requests, signed JWTs from the real token service, and the existing
isolated PostgreSQL Testcontainer. `/api/auth/me` verifies JWT A really resolves
to A and an anonymous client receives 401. `TeamApiBaselineScenarioBuilder`
extends `TwoTeamSecurityScenarioBuilder` with team-B Admin/Member, news A/B,
tables A/B, events A/B and protocols A/B. Every case gets fresh resources.
Public cases explicitly make B public. Random seed IDs/names prevent collisions;
assertions use exact scenario IDs and query persisted state in fresh scopes.
Uploads use a tiny synthetic PNG and a spy storage service; no external storage.

## Authorization matrix

Here `A` owns private team A; `B` owns team B. Admin/Member belong to B.
"Honest query" means the supplied ID matches the JWT. These role checks are
conditional guarantees only: changing the query defeats them today.

| Surface | Honest/current boundary | Reproduced insecure baseline |
|---|---|---|
| `GET /api/teams?currentUserId=B` | Missing actor 400; returns B's memberships | Anonymous or JWT A gets B's team and owner invite, 200 |
| `GET /api/teams/public` | Public B included; private A excluded; invite hidden | No new security claim |
| `GET /api/teams/{B}` | Missing team 404; absent/member query hides invite | Anonymous/foreign JWT can read private B, 200; query B reveals owner role/invite |
| `GET /api/teams/{B}/members` | Missing team handled by existing controller | Anonymous/foreign JWT reads B's three member profiles, 200 |
| `GET /api/teams/{B}/news` and `GET /api/news?currentUserId=B` | Missing feed actor 400 | Anonymous/foreign JWT reads private news, 200; query B yields `canManage=true` |
| `POST /api/teams?currentUserId=B` | Existing name produces 409 contract | Anonymous/JWT A creates a team owned by B, 201; owner membership verified |
| `PUT /api/teams/{B}` | Owner/Admin 200; Member/foreign honest query 403 with unchanged description | Anonymous/JWT A plus query B edits B, 200 |
| `POST /api/teams/join-by-code` | Repeat membership is idempotent | Anonymous/JWT A enrolls B into A via query B, 200 |
| `POST /api/teams/{B}/join-public` | Private team 400 | Anonymous/JWT A enrolls a different user into public B, 200; jersey persisted |
| `PUT /api/teams/{A}/members/me/number` | Successful number persisted | Anonymous/JWT A edits B's number via query B, 200 |
| `DELETE /api/teams/{A}/members/me` | Owner with other members 400 | Anonymous/JWT A removes B via query B, 204; last owner can leave an extant ownerless team, 204 |
| `PUT /api/teams/{B}/members/{member}` | Owner can change role; Admin can edit badge but cannot change role; Member/foreign 403; owner role change/transfer 400 | Anonymous/JWT A plus query B edits another member, 200 |
| `DELETE /api/teams/{B}/members/{member}` | Owner/Admin can remove member; Admin cannot remove peer Admin (403); owner removal/self-removal 400; foreign target 404 | Anonymous/JWT A plus query B removes member, 204 |
| `POST/PUT/DELETE /api/teams/{B}/news[/{newsB}]` | Owner/Admin allowed; Member/foreign 403; news A under B route 404, unchanged | Anonymous/JWT A plus query B creates/edits/deletes news, 200/200/204 |
| `POST /api/teams/{B}/avatar/upload`, `/cover/upload`, `/news/upload-image` | Owner/Admin allowed; Member/foreign 403 and zero storage calls | Anonymous/JWT A plus query B uploads, 200; spy call and applicable team URL persisted |
| `GET /api/teams/{B}/tables[/{tableB}]`, `GET /api/events/{eventB}/table-protocols` | Member/Owner/Admin reads 200; foreign or no query 403 even on public B | Anonymous/JWT A plus query B reads private tables/protocols, 200 |
| `GET /api/news/tables?currentUserId=B` | Membership-filtered feed | Anonymous/JWT A receives B's table with management flag, 200 |
| `POST /api/teams/{B}/tables`, `POST /api/events/{eventB}/table-protocols` | Owner/Admin management; Member/foreign/no query 403; duplicate protocol 409; foreign table 404 | Anonymous/JWT A plus query B creates resources attributed to B, 200 |
| `PUT /api/events/{eventB}/table-protocols/{protocolB}` and `/rows/{rowB}` | Owner/Admin allowed, others 403; wrong event/protocol or foreign row 404; unchanged foreign protocol | Anonymous/JWT A plus query B bulk-updates B's stats, 200, including aggregate table totals |
| `GET /api/teams/{A}/tables/{tableB}?currentUserId=A` | Response is 404 | **Before returning 404, inserts A's member into B's table**; verified row count 3 -> 4 |

## Debt and follow-up assertions

- **SEC-001:** all query impersonation and anonymous private-resource exposure
  above. HP-80 should flip authentication/actor expectations while retaining
  success/error DTO compatibility. TeamTables has the same trust problem.
- **ARC-002 / ARC-004:** direct controller persistence and mixed role decisions
  are unchanged. HP-81/82 extraction must keep compatible results; HP-83 must
  address table/protocol authorization, especially synchronization before the
  team/table match. The 404 write is concrete additional evidence in this slice,
  not a claim that returning 404 protects the database.
- **TECH-001:** last-owner leave returns 204 while the team remains. Owner with
  others is blocked. The owner-invariant follow-up must flip the ownerless case;
  HP-79 does not introduce a transfer or deletion policy.
- **TECH-002 / TECH-003:** roster uniqueness and attendance atomicity remain
  outside this slice. Existing M2/attendance suites continue to run; these new
  tests make no concurrency or transaction guarantee for those debts.
- **PERF-001:** GetEvent collection loading is unchanged and not benchmarked.
  No performance or architecture debt is marked resolved.

## HTTP contract and frontend pairing

`ConsumerContractTests` captures real TeamDto, TeamMemberDto, TeamNewsDto and
team 400/403/404/409 responses. Synthetic IDs and news timestamps are fixed at
seed time; only error correlation IDs are normalized. The existing real auth
401 remains in the bundle: no fake team 401 is manufactured. See
[contract regeneration](quality-contracts.md). CI never regenerates fixtures.
The exact bundle is copied to the frontend same-named task branch.

Frontend `teams.test.ts` freezes URLs, query fallback/explicit actor selection,
encoded resource IDs, methods, JSON/multipart bodies and 204 handling.
`teamBackendContract.test.ts` uses the generated responses through production
team consumers. The team-page hook test verifies the page's user is passed
separately from team/member resources even if cached user differs. This is the
intentional pre-JWT migration contract; localStorage is not trusted identity.

## Verification and limits

Run `dotnet test --filter Category=HP79` and the contract test, then the full
solution suite and Python quality tests under the repository contribution rules.
No shared staging writes, deployment, migrations, runtime/auth changes or
production configuration changes are part of this task. Local/CI results and
exact final heads belong in the Draft PR and author handoff. Independent review
is pending a different session; author checks are not independent approval.
Rollback removes these tests/docs/paired fixtures together without runtime impact.
