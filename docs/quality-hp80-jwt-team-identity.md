# HP-80: TeamsController JWT identity

Branch: `quality/hp-80-jwt-team-identity` in both repositories.
Backend base: `ff570b4a38af5f9316e6e4e558a2118a4e77cbe8`.
Frontend base: `7622f997a8d2671c3b21dbb35c39aa309b90d3b1`.

TeamsController injects the existing `ICurrentUser`. Class-level `Authorize`
protects my-teams/news feeds and every mutation. Each protected action rejects
unresolved or empty JWT identity with 401. Actor query parameters are removed
from action signatures; unknown legacy `currentUserId` queries are ignored,
including malformed values. No query/JWT equality rule or fallback exists.

`AllowAnonymous` preserves the public directory and PWA logo. Team details,
members and news support optional authentication with this visibility matrix:

| Viewer | Public team | Private team |
|---|---|---|
| Anonymous | 200 | 401 |
| JWT foreign user | 200 | 403 |
| JWT Owner/Admin/Member | 200 | 200 |

Missing teams return 404 before the visibility check. For details, role, badge,
jersey and Owner/Admin invite visibility derive from JWT membership only.
News management flags derive from JWT team roles. Existing member-role guards,
target-resource substitution 404s, validation/conflict 400/409s and TECH-001
owner-leave behavior remain covered. Route member `userId` is still a target.

## Security evidence

`TeamApiBaselineTests` now contains HP-80 regression expectations for real signed
JWTs and anonymous requests. Coverage includes both visibility matrices,
owner/admin/member/foreign projections, protected feeds without query identity,
spoofed owner mutations, malformed/ambiguous/missing/empty signed JWT identity,
JWT-owned creation, private/public joining, idempotent joining, self-number and
self-leave. Each mutation proof reads persistence through a fresh DB scope.
News creation verifies `AuthorUserId` for Owner and Admin with a foreign query.

The media section of `TeamMediaAndTablesBaselineTests` requires Owner/Admin JWT
success, Member/foreign/spoofed denial (403), anonymous denial (401), zero storage
calls for denial, and fresh-scope avatar/cover persistence. Only synthetic PNGs
and fake storage are used. The PWA logo remains publicly reachable.

`ConsumerContractTests` exports actual HTTP DTOs and genuine team 401/403/404,
name-validation 400 and duplicate-name 409. Only the existing nondeterministic
correlation ID is normalized. The frontend fixture is copied byte-for-byte
using [the established procedure](quality-contracts.md).

Frontend TeamsController APIs reuse existing authFetch bearer/refresh transport
with a ten-second timeout, and remove actor arguments and cache fallback; callers
and authenticated E2E setup use the account session. API tests retain encoding,
methods, bodies, multipart, abort signals, 204 and jersey null/zero coverage.
Different page/resource/target/cached IDs prove that cached identity cannot
change migrated request URLs.

## Deliberately deferred

SEC-001 is fixed only for TeamsController by HP-80. TeamTablesController still
trusts `currentUserId` until HP-83. Its production code and insecure baseline
assertions are unchanged. All eight frontend table/protocol functions retain
their legacy actor query and fallback. SEC-001 therefore remains Open globally.

AppDbContext/controller use cases (ARC-002/ARC-004), owner invariant (TECH-001),
roster/attendance work and EventService performance are outside this change.
No schema, migrations, dependencies, production/release configuration or HP-87
audit baseline change is included. ADR-003 is followed without amendment.

## Verification and handoff

Focused security/contracts precede the full PostgreSQL suite, skipped-test gate,
Debug/Release builds and Python quality tests. Frontend focused/full Jest,
production build, Python quality and isolated full-stack Playwright are required.
Final results, heads and exact CI runs belong in the paired Draft PRs/handoff.
Independent review is pending a separate session; author checks are not approval.
HP-80 stays In Progress; a human owns merge. Rollback reverts the paired commits
together without a database/configuration rollback; it reopens Teams exposure.
