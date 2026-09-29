# HP-75: post-deploy staging contract

Local implementation status: **READY FOR REMOTE VALIDATION**. No remote wrapper
installation/output, Actions or deployment is certified by local tests.

## Graph and identity

Each trusted develop push runs `validation -> deploy -> smoke`. Smoke cannot run
after a failed deploy, and cannot make a failed pipeline green. SSH deploy failure
is DEPLOY FAILURE (deploy job); install/runtime failure is TEST INFRA FAILURE;
the orchestrator separately reports POST-DEPLOY HEALTH FAILURE / ENVIRONMENT STATE
FAILURE in a sanitized summary. Backend deploy now supplies the full git SHA.

Backend smoke requires its expected SHA; frontend must boot but need not have a
new SHA. Frontend smoke checks its own full SHA/HTML/bundle digest and only observes
backend SHA. Expected migration IDs come from the **deployed backend revision**
via local git history, not the newer checkout. Historical baseline IDs are always
required. Missing, extra, duplicate or malformed history fails closed. No SQL
migration is executed by smoke.

Public endpoints are fixed to https://staging.hockeyplanner.ru:
`/api/health` must be 200/Healthy; `/api/version` must be valid JSON, Staging and
the expected backend commit (legacy short SHA accepted only as a prefix, resolved
by git when deriving migrations). Frontend checks `/build-meta.json`, `/login`
and the referenced main JS SHA-256. Redirects are not followed by HTTP diagnostics.

## Cross-repo race policy

Repo-local concurrency covers validation, deploy AND smoke, with cancellation
disabled. GitHub concurrency does not cross repository boundaries. Across repos,
bounded retries tolerate temporary HTTP/container/migration/queue changes.
If the counterpart commit is newer than checkout, only the fixed origin/develop
ref is fetched read-only; HTTP metadata never controls a remote URL/fetch ref.
Two healthy samples bracket browser smoke, separated by at least three seconds;
backend/frontend identities must stay stable. At most 30 attempts, ten-minute
scheduling budget, 8s HTTP / 25s wrapper / 65s browser timeouts; an in-flight sample
can exceed the scheduling budget only by its bounded I/O duration. Job timeout20m.
This is point-in-time verification, not a lock against a later deployment.
Long outages or continuous activity intentionally fail, never loop forever.

## Restricted diagnostics: operator proposal, not installed remotely

Only these exact no-argument commands are invoked as restricted `codex`:

```
sudo /usr/local/bin/hp-staging-status
sudo /usr/local/bin/hp-staging-migrations
sudo /usr/local/bin/hp-staging-queue
sudo /usr/local/bin/hp-staging-logs
```

Existing wrapper output was not available locally. Before remote validation an
operator must verify/adopt the following **JSON v1 stdout contract**. Do not grant
generic docker/psql/bash or mutate VPS from this repository's smoke. Wrappers must
remain pinned to staging resources, read-only, timeout-bounded and exit nonzero
on query/inspection failure; no banners mixed into JSON. No inspect environment,
credentials, recipient data or notification payload should be emitted.

`hp-staging-status`:

```json
{"schemaVersion":1,"backend":{"state":"running","health":"healthy","restarting":false,"restartCount":0},"postgres":{"state":"running","health":"healthy","restarting":false,"restartCount":0}}
```

Both must be running, healthy, not restarting and restartCount=0. A prior restart
is deliberately an operator-visible environment failure, not silently ignored.

`hp-staging-migrations`:

```json
{"schemaVersion":1,"migrations":["20260125121252_InitialCreate"]}
```

Example abbreviated; real list must match all expected IDs (currently43). Required
baseline/M6 IDs:

- 20260125121252_InitialCreate
- 20260125125623_Attendance_RenameFieldToUser
- 20260125133940_Line_RenameFieldToPlayers
- 20260928151908_AddNotificationJobs
- 20260928152829_AddNotificationLogicalIdentity
- 20260928154002_AddDurableEmailAndLeagueNotificationWork

`hp-staging-queue`:

```json
{"schemaVersion":1,"total_jobs":0,"pending":0,"processing_claimed":0,"completed":0,"retrying":0,"terminal_failed":0,"max_attempt_count":0,"oldest_pending_age_seconds":null,"oldest_processing_age_seconds":null}
```

Definitions from actual `notification_jobs`: status0 Pending,1 Processing,2
Succeeded,3 Failed. Retry = status0 and attempt_count>0. Pending age uses created_at;
processing age uses claimed_at (fallback created_at if missing). NULL ages only
for empty groups. Counts include every job kind. Operator may use this read-only
aggregate in the **staging-only wrapper**, never production:

```sql
SELECT json_build_object(
 'schemaVersion', 1, 'total_jobs', count(*),
 'pending', count(*) FILTER (WHERE status = 0),
 'processing_claimed', count(*) FILTER (WHERE status = 1),
 'completed', count(*) FILTER (WHERE status = 2),
 'retrying', count(*) FILTER (WHERE status = 0 AND attempt_count > 0),
 'terminal_failed', count(*) FILTER (WHERE status = 3),
 'max_attempt_count', coalesce(max(attempt_count), 0),
 'oldest_pending_age_seconds', CASE WHEN count(*) FILTER (WHERE status = 0) > 0
   THEN greatest(0, extract(epoch FROM now() - min(created_at) FILTER (WHERE status = 0))) END,
 'oldest_processing_age_seconds', CASE WHEN count(*) FILTER (WHERE status = 1) > 0
   THEN greatest(0, extract(epoch FROM now() - min(coalesce(claimed_at, created_at)) FILTER (WHERE status = 1))) END
) FROM notification_jobs;
```

CASE preserves NULL ages for empty groups. Do not turn missing age data into0.
Policy: terminal_failed>0 fails; processing must drain to0 for PASS (retry while
busy); processing age>300s fails; pending age>900s fails; retrying>25 fails.
Retrying1..25 with acceptable age is a warning, not failure. No payload is read.

`hp-staging-logs`: existing text output is accepted **only on failure**. Raw text
stays in memory and is reduced to three regex counters; no lines, exceptions,
URLs or IDs are printed/artifacted. These counters are diagnostics, not proof
that every log exception is a regression. Operator still reviews relevant logs.

## Security / artifacts

Environment `staging-smoke` holds STAGING_DIAGNOSTIC_HOST, STAGING_DIAGNOSTIC_KEY,
STAGING_KNOWN_HOSTS. Use a dedicated restricted SSH key, never VPS_SSH_KEY. Known
host keys must be pinned through a trusted channel, never runtime ssh-keyscan.
Only push/develop/exact repository jobs use these secrets. PR validation uses
local fixtures and no staging secrets. Configure environment branch protection.

Raw HTTP/SSH output is not persisted. Summary contains validated aggregate fields
and fixed failure codes only. Browser receives an allowlisted environment without
SSH secrets. Browser artifacts are data-minimized in frontend tooling; only safe
summary and sanitized-marker browser directories uploaded, seven-day retention.
Do not upload arbitrary working directories or raw subprocess stdout/stderr.

## Local / remote validation

`python -m unittest discover -s scripts/quality -p 'test_*.py'` proves health/SHA,
missing history, terminal queue, bounded retry, cross-repo identity and SSH safety.
Frontend local Chromium fixture tests prove ErrorBoundary/runtime/unhealthy fail
and real production-build boot. Real HP-73 backend/DB/browser tests stay separate.

Remote prerequisites: both companion HP-75 commits available; narrow wrappers
verified/adopted by operator; protected environment/three secrets configured;
staging frontend bind mount checked. Only after explicit authorization publish
and run both deploy workflows. Inspect each own SHA, graph, migrations/queue and
sanitized artifacts. Remain In Progress until remote success. No push/deploy or
wrapper changes are authorized by this document.
