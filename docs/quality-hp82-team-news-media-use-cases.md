# HP-82 team news and media use cases

Author base: `ed898b81ddfca65ff4869b0626e2d2236609fb3d`.
Branch: `quality/hp-82-team-news-media-use-cases`.
HP-82 remains In Progress; independent review is pending. This is author evidence.
The Draft PR records the exact head, changed-file count, CI and review handoff.

## Minimal boundary

Application `ITeamNewsService` / `TeamNewsService` owns list, membership feed,
create, update and delete, including authorization, normalization and EF work.
Application `ITeamMediaService` / `TeamMediaService` owns media authorization
and avatar/cover persistence. Both reuse HP-81 `CoreTeamRolePolicy`. The existing
core visibility and TeamDto mapping helpers become internal so news/media can
reuse them without changing core behavior or duplicating their serialization.

WebAPI `ITeamMediaUploadService` / `TeamMediaUploadService` owns the existing
IFormFile validation, stream lifetime and IFileStorageService call. It delegates
authorization/persistence to Application and has no EF or team-role decisions.
This retains the storage abstraction where it already lives, requires no new
project/package dependency, and avoids an Application -> WebAPI dependency.
Application's existing Infrastructure reference remains ARC-001 / M12.

TeamNewsDto, CreateTeamNewsRequest and UpdateTeamNewsRequest move unchanged
to Shared. TeamDto and UploadTeamImageResponse keep their existing fields and
defaults. There are no schema, migration, configuration, route or enum changes.

Migrated actions: GetTeamNews, GetNewsFeed, CreateTeamNews, UpdateTeamNews,
DeleteTeamNews, UploadTeamAvatar, UploadTeamCover and UploadTeamNewsImage.
The controller UploadTeamMedia and CanManageTeamAsync helpers are removed.
Each action binds input, reads ICurrentUser, calls one service and returns DTO
or status. All asynchronous application EF and storage calls receive the token.
New mutation timestamps use TimeProvider. PWA logo behavior is untouched.

## Characterized authorization and precedence

The original controller passed 33 new PostgreSQL/HTTP characterization cases
before extraction. Existing HP-79/80/81 JWT security regressions remain active.

| Route/operation | Anonymous | Foreign JWT | Member | Admin | Owner |
|---|---|---|---|---|---|
| Existing private team news | 401 | 403 | 200 / CanManage false | 200 / true | 200 / true |
| Existing public team news | 200 / false | 200 / false | 200 / false | 200 / true | 200 / true |
| News feed | 401 | Only own membership teams | Membership teams / false | Membership teams / true | Membership teams / true |
| Valid news create/update | 401 | 403 | 403 | 200 | 200 |
| News delete | 401 | 403 | 403 | 204 | 204 |
| Valid avatar/cover/news-image upload | 401 | 403 | 403 | 200 | 200 |

Feed `/api/news` is not a public global feed: public teams without membership
remain excluded. Team list remains newest-first, Take(50); feed Take(100).
An authenticated identity without a usable canonical JWT ID receives 401 for
existing team news, even on public teams. Missing team news reads return 404
before optional-auth visibility; missing feed users return 404.

Create validates required title/body before role lookup (authenticated invalid
input returns 400 even for a foreign actor). Update authorizes before validation,
then checks the news ID scoped to the route team. Delete authorizes before news
lookup. Missing/foreign news IDs yield 404 for an authorized manager and 403
for an unauthorized authenticated actor. Valid create for a missing team is 403.
Avatar/cover lookup a missing team first (404); news-image uses membership
authorization first (missing team 403). Anonymous protected routes remain 401.
Query/body currentUserId and authorUserId cannot grant access or change the JWT
author. Title/body/URL normalization retains 120/2000/500 character bounds.

## News transaction and notification truthfulness

Previously the pending TeamNews entity was added before NotifyTeamAsync;
NotificationOutbox could save and commit that entity along with its jobs, before
the controller's final SaveChangesAsync. The transaction owner was implicit and
a later request-path failure could follow that commit.

CreateTeamNews now opens one explicit transaction, saves news, optionally calls
the existing INotificationService to stage M6 notification/job intent, and commits
once. NotificationOutbox sees CurrentTransaction and joins it; it does not own a
second commit. HTTP 200 is returned after successful commit. With notifications
disabled, no notifications/jobs are created. Type/category/title/body/team URL
and recipients remain unchanged. No direct provider or network delivery is added.

Real PostgreSQL HTTP tests inject failures at news INSERT, notification INSERT,
job INSERT, after the actual durable enqueue, and before transaction commit.
They prove the selected fault was reached and verify from a fresh DbContext that
no new news, notification or job survived. The after-enqueue test verifies the
existing outbox has staged all three recipient jobs inside the outer transaction
before failing. Commit failure is injected before the driver commit; these tests
do not claim to simulate ambiguous acknowledgement loss after a server commit.

A successful HTTP create is independently verified with three pending jobs and
zero push calls. A later, separate-scope NotificationJobProcessor run uses a
failing provider, with no DB transaction spanning delivery. News and its successful
HTTP result remain intact; the existing worker persists provider_transient and
retry state. Existing M6 retry and at-least-once delivery semantics are unchanged.

## Upload compatibility

The endpoint RequestSizeLimit and application file limit remain 5 * 1024 * 1024.
Empty input, excessive size, non-image MIME and disallowed extension still fail
with 400 before storage. Existing validation accepts case-insensitive image/*
and .jpg/.jpeg/.png/.webp/.gif, including case-insensitive extensions. This task
preserves that validation; it adds no file signature/provider redesign.

Avatar and cover use FileStorageFolders.Teams; news images use News. ScopeId
comes from the authorized route GUID formatted N, never a client storage key.
Storage receives the same filename, content type, stream and cancellation token.
Avatar/cover return the existing TeamDto, including the historical null
MyTeamJerseyNumber projection. News image returns exactly { imageUrl }.
BusinessRuleException maps to safe 400; other non-cancellation storage/persistence
failures retain sanitized 502 through the M5 error pipeline. Request cancellation
propagates instead of being relabeled 502. Storage IO occurs outside DB transactions.
Replaced objects are not deleted; storage-success/database-failure orphan handling
remains INF-006/M9.

## Review follow-up: preserve the authorized media snapshot

Independent review of `6fec375201858948165a816fedbbd7506511372d` returned
CHANGES REQUIRED for one MEDIUM avatar/cover race. The second tracking Include
after storage could fix up a replacement membership alongside the original tracked
membership. The media URL then committed before a post-save Single lookup threw,
causing HTTP 502 for a successful mutation.

Scoped TeamMediaService now retains the initially authorized Team and actor
membership together. SaveTeamMedia checks that the prepared snapshot matches
the team/actor arguments, then reuses it for persistence and DTO mapping. It does
not reload memberships or select the actor from the navigation after saving.
The storage abstraction, upload orchestrator, authorization policy, news-image
behavior, validation, token propagation, clock and DTO semantics are unchanged.

TeamMediaSnapshotRaceTests covers avatar and cover through real JWT HTTP and
PostgreSQL. A TaskCompletionSource gate signals storage entry and holds the request
while a separate DbContext deletes/saves the original Admin membership, then
inserts/saves a new Admin membership for the same team/user with a different PK.
After release, fresh-context checks verify the URL, one current membership and
the replacement PK; the response must be 200 with the original snapshot's badge
and member count. A request-only command interceptor asserts exactly one
membership-loading query. No sleeps or weaker First-after-reload workaround.

Red/green was captured: with only the new tests added to reviewed head 6fec375,
both cases verified the committed URL/current membership, then failed because the
response was BadGateway rather than OK. After the service fix both cases pass.
Follow-up verification: both race cases pass; HP-82 focused 52/52; combined
HP-79/80/81/82 and contract regressions 147/147; full PostgreSQL 650/650 with zero
skips and the no-skips/missing-suite gate passing; migration readiness 5/5;
Python quality 31/31; Debug and Release builds pass with the same 21 existing
NuGet warnings and zero errors. Contract generation/comparison has no drift:
canonical SHA256 `4c6014591d04c3f57440b9633b135fdee984e3be7a74c576fa6acb3597520936`,
Windows generated SHA256 `4bf24556d8855d2e09dc853ff9022389faf312807d73dc0432f9fc153d63fe06`.
The follow-up commit/head and exact-head CI belong in PR #19.
Independent re-review is pending; the PR remains Draft and HP-82 In Progress.

## Initial author verification and review handoff

- Baseline restore/build and full suite: 598 passed, 0 failed/skipped.
- Focused HP-82: 50 passed (38 HTTP, 12 use-case/fault/cancellation cases).
- Combined HP-79/80/81/82 and consumer-contract run: 145 passed, 0 failed/skipped.
- Final restore and Debug/Release builds passed (21 pre-existing NuGet warnings,
  zero errors in each build); full PostgreSQL suite: 648 passed, zero failed/skipped.
  MigrationReadinessTests: 5 passed, including fresh and existing database paths.
  check_test_results.py passed: 648 passed, zero failed/skipped/missing suite.
- Python quality suite: 31 passed, including three deterministic architecture
  checks. The guard covers all of TeamsController because no DbContext paths remain.
- No sleeps, skipped tests, EF InMemory or production/staging DB access introduced.
- ConsumerContractTests exports live HTTP with HP_CONTRACT_OUTPUT only; comparison
  to the existing committed fixture passes without HP_UPDATE_CONTRACT.
- Existing compare_contract.py passes against frontend develop
  `d9fb43fe8099e2208e7c06b4a7e9f720359d3b57`, fetched into ignored backend test
  artifacts. No frontend checkout or files were changed.
- Canonical Git fixture SHA256:
  `4c6014591d04c3f57440b9633b135fdee984e3be7a74c576fa6acb3597520936`.
  Windows checkout/generated CRLF SHA256:
  `4bf24556d8855d2e09dc853ff9022389faf312807d73dc0432f9fc153d63fe06`.
  Generated equals local committed bytes; backend/frontend Git blobs are identical.

HTTP tests are the automated local smoke for all migrated journeys. Live provider,
browser/staging smoke and post-merge verification are not performed by this author
task; no deployment is authorized. Existing NuGet vulnerability warnings remain
unchanged and no dependency upgrade is included. Full diff and review checklist
are self-reviewed for scope, identity, API, transactions, cancellation and rollback;
self-review is not independent approval. The repository has no M7 GitHub tracking
issue available; focused evidence resides here and in the Draft PR, without writing
to the unrelated M2 tracking issue or changing Linear status.

Independent reviewer must verify base/head, inspect the complete diff and affected
code/tests, rerun relevant checks, and assess correctness, security, contracts,
transaction failure windows, concurrency and test realism. No migration, workflow,
deployment or ownership-transfer changes are included. CI and independent approval
must cover the final head before a human decides to mark ready or merge.

## Remaining debt and rollback

Remaining direct TeamsController DbContext paths: none. HP-83's separate
TeamTablesController remains unchanged: GetTeamTables, GetTablesFeed,
GetTeamTable, CreateTeamTable, GetEventProtocols, CreateEventProtocol,
UpdateProtocol, UpdateProtocolRow, SyncTableRowsAsync, RecalculateTableAsync,
CanSeeTeamAsync, CanManageTeamAsync and GetTeamJerseyNumbersAsync.
SEC-001's TeamTables remainder stays Open for HP-83. ARC-002/ARC-004 and global
TECH-006 stay Open; only the HP-82 slice is documented as extracted/protected.
ARC-001/M12, INF-006/M9, HP-84/85 and HP-87/88 remain untouched.

Rollback is one backend commit revert (all services/DI/contracts/controller/tests
together), with no database or frontend rollback. It restores the previous implicit
news/outbox transaction ownership and therefore needs an explicit safety decision.
No master, VERSION, release/tag, force-push, deployment or frontend change is part
of this work. HP-82 stays In Progress and the PR stays Draft.
