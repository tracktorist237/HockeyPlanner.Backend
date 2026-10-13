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
checks used Engine23.0.5/Compose2.17.3. Owner-provided read-only inventory reports
VPS Engine29.1.3/Compose2.40.3/runc1.3.4, Ubuntu24.04.4/kernel6.8.0-134 and
Python3.12.3. This implementation accessed no VPS. Round 7 admits ONLY these
rootful Linux tuples, preserving the independently reviewed legacy model:

| Profile | Engine | Compose | stock runc | Image store |
| --- | --- | --- | --- | --- |
| Legacy | 23.0.5 | 2.17.3 | 1.1.5 | classic overlay2 graphdriver |
| Docker29 candidate | 29.1.3 | 2.40.3 | 1.3.4 | classic overlay2 graphdriver |

Arbitrary cross-combinations/unknown versions block. The helper checks the tuple,
default runtime `runc`, stock `path=runc`, no runtime arguments, rootful stock
security options and image-store model before build, immediately before up and
after up. Switching even to the other supported profile during a transaction
blocks. Custom runtimes/arguments, rootless, privileged, custom security options
and GPU/device injection are unsupported. These source-reviewed pins are a
compatibility boundary, not an instruction to install/downgrade VPS software.
Actual binaries, stock hooks
and protected daemon configuration require separate operator verification;
version/inspect output does not attest a malicious daemon. Full prefixed
IDs are intentional; bare64hex names have had Compose parsing regressions.
A snapshot must round-trip through installed Compose unchanged (including `$`
values); incompatibility is BLOCKED, not permission to fall back to tags.
Do not upgrade the VPS as part of this runbook.

Docker29 `Runtimes.runc.status` is an optional map containing only
`org.opencontainers.runtime-spec.features`, whose value is JSON capability
metadata from `runc features`. The closed runc1.3.4 Features schema is validated,
including nested types, known hook names and duplicate-key rejection; unknown
status/execution fields, malformed metadata or runtime arguments block. Advertised
hook names and annotation strings are passive metadata, never installed hook
programs, execution configuration or binary attestation. Empty/absent status can
represent unavailable capability output; it does not waive stock-binary/hook
operator verification. The legacy runtime representation remains path/args only.

**Image-store boundary:** Moby29's classic graphdriver retains config-hash IID
and uncompressed Docker-save, but its exact tarexport/save.go now writes config
and raw layers to `blobs/sha256/<digest>` plus a Docker-compatible manifest and
OCI wrapper metadata. The scanner accepts only the old hash.json or this exact
config-hash path, verifies each blob layer path/hash AND raw DiffID, and retains
all historical/final/pin/rootfs checks. OCI index/manifest digests never substitute
for the expected config IID. Its containerd image service instead
inspects a manifest/index digest and exports OCI blob paths/compressed content
with a Docker-compatible manifest. That is a different identity/export model.
Round 7 rejects non-overlay2 drivers and any `DriverStatus` driver-type marker
before build, rather than comparing a manifest ID to a config ID or weakening
the scanner. Actual VPS image-store choice was not supplied/verified. If it is
containerd-backed, readiness is BLOCKED and a separately reviewed implementation
of that archive/identity model is needed; no image-store change is authorized.
Historical/final leakage, config IID, DiffIDs and DLL pin checks remain unchanged.

Portable official Compose2.40.3 (published SHA256 checked) was tested locally on
WSL without daemon operations: boolean init:false, required build-secret mapping,
full sha256 image string, dollar escaping and private JSON config round-trip
equality passed. Source review confirms direct Init mapping, ImageInspect by
image reference and pull-never handling. Native Engine29/runc1.3.4 creation,
actual package/binary integrity, full up/dependency behavior and a licensed
VPS build/export/scan remain unverified operator prerequisites. Buildx was not
installed; no local or remote staging build-only/deployment was performed.

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

**Init policy (Round 6, Option A):** the resolved backend service must contain
literal boolean `init: false`. Missing/null/true/non-boolean values are BLOCKED,
including build-only mode. The helper never inserts or rewrites this setting.
Before adopting it, the owner must separately approve any change from existing
staging behavior in the private override and existing container. This runbook
does not authorize that operational change or a container recreation.
The existing backend's `docker container inspect` must already show
`HostConfig.Init` explicitly false, `Runtime=runc`, and `Privileged=false`;
nil/missing/true Init blocks before build/up. The replacement must show the same
after up; failure retains the durable barrier/private inputs and never reports
PASS. Post-up verification cannot undo a startup and performs no automatic stop.

Moby23.0.5 and29.1.3 inject a read-only bind at `/sbin/docker-init` for private PID
namespaces when HostConfig.Init is true, or nil with daemon default-init enabled.
Compose2.17.3 and2.40.3 map service.Init directly to HostConfig.Init. Literal false excludes
both cases regardless of daemon default-init; querying/inferencing the daemon
default is unnecessary. `inspect.Mounts` is derived from Container.MountPoints,
while init is appended separately to the OCI spec. An empty Mounts array is never
accepted as evidence that implicit init is absent. runc1.1.5 resolves a bind's
mountpoint in the container root through symlinks, including final components:
`/sbin/docker-init -> /app/SixLabors.ImageSharp.dll` or a chain to deps can shadow
the verified file. Such inert links may remain in an image only because init
is explicitly excluded; the exact-IID scanner still resolves all permitted
explicit mounts and managed paths against that image. Init-enabled images,
including otherwise safe images, are deliberately unsupported in this slice.

Create only the approved deploy-owned0600 override outside backend-src:
`/opt/hockeyplanner-staging/compose.imagesharp-license.yml`. Preserve base context,
ports/env/network/volumes and other services:

```yaml
services:
  backend:
    init: false # Requires separate operator authorization/reconciliation first.
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

Lexical checks alone do not prove mount safety: an image-root symlink such as
`/safe-alias -> app` can direct a seemingly unrelated mount onto `/app`. After
the mandatory exact-IID scan, the SAME final reconstructed filesystem resolves
every backend Compose bind/volume/tmpfs/secret/config target and image-declared
volume. Absolute symlinks restart at container root; relative targets start at
the link's parent. Components and dotdot are evaluated after preceding links,
with root-clamped POSIX semantics, never host `Path.resolve()`. Final-component
links, ancestor links and chains are included. Protection applies to the resolved
DLL/deps paths and their ancestors as well as the lexical destinations.

The supported model permits proven-safe aliases and ordinary absent mountpoints
that the runtime can create. Dangling links, cycles/over40 link expansions,
over4096 component steps/characters per path, ambiguous hardlink destinations,
file ancestors and source/destination file-directory conflicts block. Symlinks
elsewhere in the image are permitted. Whiteouts and later replacements determine
the final proof; a removed historical unsafe link alone does not cause rejection.
Historical private-license leakage scanning remains independent and unchanged.

Mounts must be independent: duplicate effective targets, nested mounts, or a
mount covering ANY lookup component of another mount (including a symlink node
before expansion) block, regardless of Compose category or runtime ordering.
No imported bind/volume tree is assumed to preserve another mount's resolution.
Docker/runc-managed `/dev`, `/proc`, `/sys`, `/etc/hosts`, `/etc/hostname` and
`/etc/resolv.conf` must resolve without relocation; custom lookups intersecting
these trees/files block. The actual runtime must use this supported default mount
model, without unmodeled mounts/hooks altering resolution. Operator compatibility
on the real VPS is NOT VERIFIED. Dependency containers have distinct rootfs
namespaces; existing dependency host-license/volume/lexical guards remain, and
mount inheritance into backend remains forbidden.

For both pinned models, default proc/dev/devpts/mqueue/shm/sysfs/cgroup mounts and
device nodes are confined to the replaced `/proc`, `/dev` and `/sys` trees;
network-generated hosts/hostname/resolv.conf use the three managed file paths.
Default masked/readonly paths are inside replaced `/proc` or `/sys`. Inspection
of the existing and replacement backend accepts only profile-specific stock
MaskedPaths/ReadonlyPaths (or nil selecting those OCI defaults). Docker29 adds
exact `/proc/interrupts` and `/sys/devices/virtual/powercap` masks. Only Docker29
also permits source-documented optional `/sys/devices/system/cpu/cpuN/thermal_throttle`
entries, with canonical nonnegative decimal N; Moby derives these from possible
CPUs whose thermal paths exist on the host. All base masks remain required.
Duplicate/non-string/malformed entries, missing base paths, arbitrary proc/sys
paths and any extra readonly path block; no generic subtree wildcard is accepted.
Custom lists, devices/DeviceRequests and unsupported runtime options block,
even if Mounts is empty. Init is the separate dynamic bind outside those managed
paths and is excluded by explicit false. Stock libnetwork's network setup hook
is part of this model; third-party OCI hooks/mount injection are not supported.
Even custom paths prefixed by `/proc` are unsupported: kernel links such as
`/proc/self/root` defeat lexical subtree containment.
Operator verification must establish a trusted stock daemon/runc setup without
unmodeled hooks; no security claim is made against a lying/modified daemon.

runc1.3.4 uses pathrs OpenInRoot/procfd mountpoints and source-type-based creation
with SecureJoin fallback for missing paths. Absolute/relative symlinks remain
container-root lookups; mounts are processed sequentially, so earlier mounts can
alter later lookup. The existing rootfs proof is deliberately stricter: dangling
links/ambiguous creation and overlapping lookup trees are rejected. Ordinary new
file/directory mountpoints and independently proven safe aliases remain supported;
source/destination type checks and protected app ancestors still apply. No general
OCI emulator or resolver rewrite was needed. Original F4 aliases and init variants
continue to be exercised with the actual synthetic archive/deployment path.

The in-memory immutable scan result contains only current IID, layer count and
verified destination/type/lookup metadata, no archive/file/license contents. It
is bound to the exact current export and backend destinations of the private
Compose snapshot. There is no layer-count-only or previous-build proof fallback.
Before up, the helper rechecks this proof, file/directory types and declared image
volumes alongside all existing source/config/IID/lock/container guards. Post-up
reuses the SAME proof and accepts only its configured or unambiguous canonical
effective destinations with the expected mount type/source. An unverified
post-up destination/IID/configuration cannot remove the recovery barrier.
Determinate pre-up rejection after completed export safely cleans private inputs;
uncertain operations/post-up failures retain them. No automatic rollback or Docker
atomicity is claimed. This is a mount-resolution proof under a trusted immutable
image/daemon/runtime setup, not a live writable-layer attestation or a guarantee
against a privileged writer or application rewriting its own filesystem.

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
types, OCI-only archives without a single compatible Docker manifest, compressed
layers, duplicate/ambiguous/corrupt archives or unknown
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

### Owner-reported inventory versus remaining operator blockers (Round 7)

The separately authorized inventory reports the existing backend running/healthy,
Runtime=runc, Privileged=false, no custom SecurityOpt/Devices or explicit mounts,
and the exact Docker29 base masks/readonly paths supported above. It also reports
HostConfig.Init=null: this remains BLOCKED before build/up, not equivalent to false.
No container was changed/recreated and no image ID was requested by this author.

The following were NOT fixed or established here and block human merge/deploy:

1. Buildx is absent; required licensed build tooling needs separate authorization.
2. Existing backend Init=null must be reconciled to explicit false only under
   separate authorization, with the private override and container state agreeing.
3. Required staging private license is absent; owner/permissions/expiry/renewal
   and safe provisioning need separate authorization.
4. Protected shared lock infrastructure is absent.
5. Private Compose override is absent.
6. All permitted staging writers' participation in the SAME lock/barrier protocol
   is not confirmed.
7. Frontend staging workflow has not adopted that protocol. Backend-only changes
   cannot establish frontend cooperation; frontend is untouched in this task.
8. Real licensed staging build and exact-image scan have not run.
9. Actual image store, native runtime/package integrity, absence of third-party
   hooks, full snapshot/up compatibility, volume backing, rollback IID and
   exceptional daemon-quiescence/recovery procedures remain unverified.

Source/synthetic/local Compose evidence establishes a bounded code compatibility
model, not VPS readiness. No infrastructure/security debt is closed by it.
**OPERATOR PREFLIGHT: NOT VERIFIED. HUMAN MERGE / DEPLOY: BLOCKED.**

References: [Build secrets](https://docs.docker.com/build/building/secrets/),
[image IDs](https://docs.docker.com/reference/cli/docker/image/pull/),
[Compose image-ID parsing](https://github.com/docker/compose/issues/12443),
[official NuGet4.1.2](https://www.nuget.org/packages/SixLabors.ImageSharp/4.1.2).
POSIX lifetime references: [Python subprocess](https://docs.python.org/3/library/subprocess.html),
[flock open-file-description inheritance](https://man7.org/linux/man-pages/man2/flock.2.html).
Container-root lookup/mount references:
[runc mountpoint lookup and ordered setup](https://github.com/opencontainers/runc/blob/main/libcontainer/rootfs_linux.go),
[root-scoped symlink semantics](https://github.com/cyphar/filepath-securejoin/blob/main/join.go),
[Moby volume setup](https://github.com/moby/moby/blob/master/daemon/volumes.go).
Round 6 source review pins:
[Moby23.0.5 init/default-init injection](https://github.com/moby/moby/blob/v23.0.5/daemon/oci_linux.go#L743-L763),
[Moby23.0.5 inspect mount origin](https://github.com/moby/moby/blob/v23.0.5/daemon/inspect.go#L56-L88),
[Moby23.0.5 default mounts/masks](https://github.com/moby/moby/blob/v23.0.5/oci/defaults.go#L45-L108),
[Compose2.17.3 Init mapping](https://github.com/docker/compose/blob/v2.17.3/pkg/compose/create.go#L356),
[runc1.1.5 bind mountpoint lookup](https://github.com/opencontainers/runc/blob/v1.1.5/libcontainer/rootfs_linux.go#L225-L254).
Round 7 exact-tag source evidence:
[Moby docker-v29.1.3 release](https://github.com/moby/moby/releases/tag/docker-v29.1.3),
[implicit mounts/masks and optional CPU paths](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/pkg/oci/defaults.go),
[init and OCI mount ordering](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/oci_linux.go),
[runtime status emission](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/info_unix.go),
[stock runtime/feature collection](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/runtime_unix.go),
[runc1.3.4 Features schema](https://github.com/opencontainers/runc/blob/v1.3.4/vendor/github.com/opencontainers/runtime-spec/specs-go/features/features.go),
[runc1.3.4 rootfs handling](https://github.com/opencontainers/runc/blob/v1.3.4/libcontainer/rootfs_linux.go),
[Compose2.40.3 create/Init](https://github.com/docker/compose/blob/v2.40.3/pkg/compose/create.go),
[Compose JSON/dollar serialization](https://github.com/docker/compose/blob/v2.40.3/cmd/compose/config.go),
[containerd inspect identity](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/containerd/image_inspect.go),
[containerd exporter](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/containerd/image_exporter.go),
[containerd driver marker](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/containerd/service.go).
[classic config IID inspection](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/images/image_inspect.go),
[classic config/raw-layer blob export](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/internal/image/tarexport/save.go).
