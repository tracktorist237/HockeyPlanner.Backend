# ImageSharp staging operator runbook — locked build-only readiness

Status: implementation only. **No operator execution is authorized yet.**
Obtain separate owner authorization before VPS access, provisioning, checkout,
image build/export or cleanup. No actual VPS readiness is asserted here.
Production, DBs, master/VERSION and HP-84 PR #21 remain outside scope.
Human merge of PR #22 requires all operator prerequisites and new exact-head review.

## 1. Confirm existing identity, tooling and shared lock protocol

Using the authorized deploy account and pinned SSH host key, privately confirm
existing project `hockeyplanner-staging`, project directory `/opt/hockeyplanner-staging`,
base `docker-compose.yml`, backend service and checkout `backend-src`. Confirm
container labels project/service/working_dir/container-number, expected name,
current image ID and no conflicting backend container. **BLOCKED on mismatch**;
never create a second project, rename a container or adapt production ad hoc.
The deploy helper requires exactly one existing running backend and checks its
identity/configuration/restart/network fingerprint again immediately before up.

Record Engine, BuildKit/buildx, Compose v2, Python3.12+, Git and util-linux flock
versions. Require local trusted builder, build secrets, `--load --iidfile`, config
JSON snapshots and full `sha256:<64hex>` local image references. Require Linux
kernel pidfd support, prctl child-subreaper and readable /proc task children. Local native
checks used Engine23.0.5/Compose2.17.3; VPS versions are NOT VERIFIED. Full prefixed
IDs are intentional; bare64hex names have had Compose parsing regressions.
A snapshot must round-trip through installed Compose unchanged (including `$`
values); incompatibility is BLOCKED, not permission to fall back to tags.
Do not upgrade the VPS as part of this runbook.

An authorized administrator must provision, outside Git/contexts, directory
`/var/lib/hockeyplanner-staging/deploy` owned by the deploy account mode0700,
and regular non-symlink `deploy.lock` owned by that account mode0600. Never unlink,
replace or recreate this inode while participants may hold it. Audit **all**
permitted deployment/operator procedures capable of changing this project,
source, base/override/.env, license, Docker images/tags or containers; every one
must acquire this SAME exclusive lock before any change and retain it throughout.
After acquiring it, every writer must also reject any `recovery-required` entry
in this directory, including incomplete files/directories/symlinks. Do not bypass,
delete or adopt an existing barrier automatically. The SSH source-preparation phase
creates an exclusive, fsynced empty directory barrier; Git inherits FD9, disables
hooks/background maintenance/GC, and only successful synchronous exact-SHA completion
removes that empty barrier. The Python transaction creates a fsynced0600 marker
before commands; it contains public PID/SHA/private-directory name only.
This includes frontend/other wrappers if they mutate this Compose project.
GitHub job concurrency alone does not provide this host-level guarantee.
Unrestricted Docker/root writers can bypass advisory locks: restrict access and
approve the shared protocol first. If any permitted writer cannot cooperate,
**BLOCKED — human decision required**; do not claim TOCTOU protection on that host.

Run the following in one authorized shell; retain FD9 for every later step:

```sh
set -eu
umask 077
LOCK=/var/lib/hockeyplanner-staging/deploy/deploy.lock
test -f "$LOCK" && test ! -L "$LOCK"
test ! -L "$(dirname "$LOCK")"
test "$(stat -c %a "$(dirname "$LOCK")")" = 700
test "$(stat -c %u "$(dirname "$LOCK")")" = "$(id -u)"
test "$(stat -c %a "$LOCK")" = 600
test "$(stat -c %u "$LOCK")" = "$(id -u)"
exec 9<>"$LOCK"
flock -n 9
test ! -e "$(dirname "$LOCK")/recovery-required"
test ! -L "$(dirname "$LOCK")/recovery-required"
```

Missing, inaccessible, busy, incorrectly owned or replaced lock means BLOCKED.
Do not ignore the error or create a different lock. Build-only approval authorizes
no up/stop/rm/restart/deploy/migration/DB command. Never execute the full SSH workflow.

## 2. Owner transfers the issued license privately, under the same lock

Use original issued `sixlabors.lic`, transferred directly by the owner through an
approved encrypted channel (e.g. SFTP with verified host identity). No pasted key,
echo, command-line value, Actions output/SSH envs, build args, chat, repository or
GitHub Secret extraction. Use a private non-shared transfer directory outside
all Git checkouts/build contexts, with umask077; no shell tracing, diagnostic
MSBuild output or binlogs. Never request or print the key.

After explicit provisioning approval, prepare
`/etc/hockeyplanner-staging/licenses` deploy-owned0700, and its regular non-symlink
`sixlabors.lic` deploy-owned0600, nonempty/readable. Protect ancestors against
untrusted replacement, and resolve outside every build context. Record valid
entitlement/expiry and renewal owner privately. Sample/expired/truncated/
inaccessible/permissive files are BLOCKED. Renewal uses the same host lock and
requires a fresh uncached build; secret contents do not invalidate BuildKit cache.

## 3. Staging-local override and exact source

Create only the approved deploy-owned0600 override outside backend-src:
`/opt/hockeyplanner-staging/compose.imagesharp-license.yml`. Preserve base context,
ports/env/network/volumes and other services:

```yaml
services:
  backend:
    build:
      dockerfile: HockeyPlanner.Backend.WebAPI/Dockerfile.staging
      secrets:
        - sixlabors_license
secrets:
  sixlabors_license:
    file: /etc/hockeyplanner-staging/licenses/sixlabors.lic
```

Supported backend build fields are context/dockerfile/secrets/final target only;
args/additional contexts/unhandled build options are BLOCKED, not silently ignored.
No license runtime secret/config/volume/environment. Runtime secrets of backend
and its dependency closure are resolved to source definitions and canonical file
paths; aliases, symlinks and hard links to the license are forbidden. Unrelated
regular file secrets are allowed. External/environment-backed or missing runtime
sources cannot be established safely and are BLOCKED. No runtime secret contents
are read by preflight.

Runtime bind trees are inspected by bounded inode identity (at most50000 walk
entries/64depth), not only canonical containment. Hardlinks to the license are
forbidden; inner symlinks, special/unreadable/ambiguous/oversized trees block.
All relevant host trees must stay protected against noncooperating modifications.
Named volumes need explicit resolved name, local driver and no options, or exactly
supported type=none,device=<absolute path> with o=`bind`, `bind,ro` or `bind,rw`. Definitions and
actual `docker volume inspect` Name/Driver/Scope/Options must match; device and actual
Mountpoint must pass the same inode walk. External/unresolved/unknown drivers/options,
missing actual volumes or inaccessible Docker-volume data block. Ordinary inspected
local database volumes are supported. Compose alone cannot prove existing volume
backing or contents; if the deploy account cannot inspect them, BLOCKED for a human
decision, not permission to add sudo, broad Docker/root access or alter DB storage.

All backend/dependency runtime bind/volume/tmpfs/secret/config destinations must be
unambiguous absolute normalized POSIX paths, preserving `/app` and both verified
files. Covering `/`, `/app`, DLL or deps is blocked. Dot/dotdot, duplicate/unknown
targets/types, volumes_from (including external container inheritance) and unmodeled
device/API-socket mounts block. Safe unrelated targets/resources remain allowed.
Image-declared anonymous mounts require accounted safe targets before up.

Set public `TASK_SHA` to the exact reviewed40hex SHA. Preparing the existing
checkout/ref for that SHA requires separate operator authorization and the same
lock. Require clean status and exact HEAD, trusted checkout ownership/permissions;
no ad hoc reset/merge/context change under build-only approval. If approved source
cannot safely be supplied, BLOCKED. The helper builds a private `git archive` of
that exact commit, rejecting links/unhandled entries, and makes exported files
read-only. It never builds the mutable checkout; HEAD/status are rechecked after build.

## 4. Run the reviewed transaction in build-only mode

Provide the existing nonsecret APP_VERSION/APP_COMMIT/APP_BUILD_TIME metadata
exactly as the deployment workflow does. Keep FD9 held in the same shell:

```sh
cd /opt/hockeyplanner-staging
TASK_BUILD_RESULT="$(python3 backend-src/scripts/staging/deploy_imagesharp.py \
  --expected-sha "$TASK_SHA" --lock-fd 9 --build-only)"
TASK_IMAGE_ID="$(printf '%s' "$TASK_BUILD_RESULT" | python3 -c \
  'import json,sys; print(json.load(sys.stdin)["imageId"])')"
unset TASK_BUILD_RESULT
```

Only sanitized JSON status/mode/imageId is printed; errors print generic BLOCKED.
The helper privately captures resolved Compose JSON including sensitive environment,
validates it, and writes a mode0400 snapshot in a random mode0700 transaction
subdirectory under the protected lock directory. Never publish/read out the
snapshot. Compose's serializer escaping is preserved verbatim and native round-trip
identity is required. Config/up/ps use this single snapshot and existing project/directory.

BuildKit buildx consumes only strictly validated build fields and frozen source,
with required secret file mount, fixed Release build/publish, --load, --no-cache
and --iidfile. It produces an isolated unique check tag, leaving the existing
service tag/container intact. The immutable IID comes from the builder, not tag
lookup; the checked snapshot's only post-build configuration change is image=IID.
Both tag and IID must inspect to that exact content ID. Source/config/snapshot/
lock/container changes cause BLOCKED before up. This removes independent mutable
Compose reads from build/up; base files are reread only to detect drift.

Every subprocess uses POSIX pass_fds=(lockFD,) and a separate session/process group.
The dedicated Linux helper is also a child subreaper: orphaned setsid/FD-closing
descendants are adopted, detected, terminated and reaped using pidfds. Unexpected
surviving descendants cause BLOCKED even when the command leader returned zero.
Unavailable primitives or unrelated children in the dedicated supervisor block.
Normal completion waits/reaps the direct child and rejects a still-live group.
SIGTERM/SIGINT/SIGHUP and timeout trigger group TERM then KILL and direct-child wait;
repeat signals are ignored while terminating/reaping. SIGKILL cannot run cleanup:
an ordinary orphan retains the inherited flock while alive; even a descendant
closing/escaping the FD cannot admit a cooperating writer because the durable
barrier remains. Runner/SSH disconnection has the same uncertainty policy. Docker
daemon operations can outlive the CLI: process exit is not proof of daemon quiescence.

Successful completion cleans private archives/snapshot/source BEFORE removing the
barrier. Determinate pre-up guard/scan failures after completed commands also clean;
command error/timeout/cancellation, owner death or unverified post-up outcome retain
the barrier and private inputs for recovery. No automatic retry or recovery option.
Build-only now performs mandatory exact-IID scanning itself before returning PASS;
it returns before up and never starts a container. A later deploy builds/scans anew.

## 5. Historical leakage and effective final filesystem checks

The helper ALWAYS exports the current builder IID via `docker image save` to a
mode0600 archive under the transaction's private0700 directory, outside Git/frozen
source/contexts, then directly invokes the existing offline scanner with that SAME
IID and private license path. Export, missing/empty/corrupt archive, mismatched IID,
scanner rejection or exception prevents up and never reports PASS. There is no
skip flag. The scanner is synchronous; after completed export/scan its temporary
archive is removed on success or scan failure. Uncertain interrupted export retains
private data for safe recovery. Never upload raw archives/configs/license/build logs.

The offline scanner executes no image/DLL code and extracts no archive paths.
Historical scan includes all layers, even files later removed, raw archive bytes,
member names/link targets/owner/group/PAX metadata, config and manifest. It checks
full private text, long lines/tokens and JSON/UTF16 forms, without printing matches.
This is byte-pattern evidence, not proof against arbitrary encryption/obfuscation.
Source/history review must additionally confirm no license ARG/ENV/COPY/runtime mount.

Independently, effective filesystem reconstruction applies ordinary/opaque
whiteouts to lower layers before current entries, directory deletion, replacement
and overwrites. Final `/app/HockeyPlanner.Backend.WebAPI.deps.json` must identify
ImageSharp4.1.2 in libraries/targets/runtime, and final `/app/SixLabors.ImageSharp.dll`
must hash to the trusted official NuGet4.1.2 net8 artifact:
`80bbde81578be31ca7864f303200b0ef188be1b3048a657546bda04afdf394d3`.
This pin was checked by repository signature verification (`dotnet nuget verify
--all`) and independent HTTPS NuGet package comparison. It proves actual DLL
identity without reflection-loading or executing it; no new parser/package is needed.
Any future legitimate DLL transformation (e.g. ReadyToRun) needs reviewed pin/strategy
changes, never an operator bypass. Unsupported required-file links, special entry
types, OCI/compressed-layer formats, duplicate/ambiguous/corrupt archives or unknown
identity are BLOCKED. Config SHA and layer DiffIDs are verified against the archive,
and saved config ID must equal the build IID; deleted historical deps cannot prove PASS.

Record only exactSHA/IID, layer count and sanitized PASS/BLOCKED. No private key,
snapshot, archive, matched bytes or raw build logs in artifacts/handoffs. Protect
exports and dispose of them under the approved cleanup procedure while holding lock.

## 6. PASS/BLOCKED, future deploy and rollback

Operator PASS requires verified existing project/container identity, every writer
on the shared lock protocol, protected lock/source/config paths, working exact
snapshot round trip, valid private license, fresh licensed build/publish, immutable
IID and complete historical/final image verification. Any missing/unverified step
is BLOCKED. Human merge additionally requires new exact-head independent APPROVE
and green Quality/Security. No issue Done until subsequent develop validation →
authorized staging deploy → HP-75 smoke.

Normal future deployment uses the same transaction with FD9 and without build-only:
lock → protected preflight/source/config → uncached build → builderIID → exact-IID
export/mandatory scan → source/config/volume/image/container revalidation → same
snapshot up --no-build --pull never → actual backend IID/labels/working_dir and full
backend/dependency mount-target/type/source/volume-name checks → ps → cleanup.
The actual mount set must match configured bindings; unexpected runtime mounts or
wrong IID are BLOCKED with barrier retained. This detects a bad daemon result after
up; it cannot retroactively prevent startup, and no automatic stop/rollback is issued.
No global name-based rm. Preserve prior verified image
ID privately for a separately approved rollback using the same project/lock and
reviewed configuration; never rollback by mutable-tag-only lookup or audit bypass.
No schema rollback is introduced. Reverting to3.1.12 restores vulnerabilities.

### Exceptional recovery (separate operator authorization required)

1. Stop further cooperating writers; obtain the SAME exclusive flock, preserve
   the stable lock inode, and keep the existing barrier. Busy lock means a live
   holder; never unlink it. Inspect protected marker/directory privately; PID alone
   may be reused and does not prove the process or daemon operation has finished.
2. Establish that the original owner, mutating children/descendants/escaped groups
   and export writers have definitely ended, and independently establish no pending
   daemon build/load/up operation can still mutate this project/images. No process
   alive is NOT sufficient daemon evidence. Use authorized host/daemon operation
   evidence; if unavailable, remain BLOCKED. Any daemon quiescence/maintenance,
   container reconciliation or rollback needs its own explicit authorization; this
   runbook grants no daemon restart, broad access, deployment or DB operation.
3. Privately reconcile actual container/project/config/IID/mount state against the
   recorded preconditions. Unexpected state requires reviewed recovery/rollback,
   not an unchecked up or automatic retry. Do not disclose snapshots/raw archives.
4. Only after confirmed quiescence and approved reconciliation, validate absolute
   ownership/non-symlink paths stay within the protected transaction directory,
   clean retained private data, then remove only the verified recovery barrier and
   fsync its parent. Never delete/recreate deploy.lock. Record sanitized recovery
   evidence. If certainty cannot be established, retain barrier: BLOCKED human decision.
5. A new authorized transaction must still build/scan/revalidate every gate. An old
   scan, recovery or rollback cannot authorize a fresh unscanned image.

Evidence categories: local synthetic regressions/native Compose/DLL-pin checks
are mechanism evidence; exact-head GitHub CI validates licensed solution Release;
**neither is real VPS readiness**. No VPS lock/adoption/versions/source/override/
license/expiry/container/volume-backing/tree/build/image-layer/recovery check was
performed in implementation. **OPERATOR PREFLIGHT: NOT VERIFIED.**

References: [Build secrets](https://docs.docker.com/build/building/secrets/),
[image IDs](https://docs.docker.com/reference/cli/docker/image/pull/),
[Compose image-ID parsing](https://github.com/docker/compose/issues/12443),
[official NuGet4.1.2](https://www.nuget.org/packages/SixLabors.ImageSharp/4.1.2).
POSIX lifetime references: [Python subprocess](https://docs.python.org/3/library/subprocess.html),
[flock open-file-description inheritance](https://man7.org/linux/man-pages/man2/flock.2.html).
