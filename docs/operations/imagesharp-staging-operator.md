# ImageSharp staging operator runbook — build-only readiness

Status: implementation authorized; **operator execution NOT authorized yet**.
This is a future procedure, not a record of VPS checks. Obtain separate owner
authorization before connecting, creating files or building on staging. Never
extract the GitHub secret. Production and HP-84 PR #21 are outside this procedure.
Human merge of Draft PR #22 requires independent review and the PASS evidence below.

## 1. Confirm identity and versions before provisioning

Use the existing authorized deployment account and pinned SSH host identity.
Confirm, through sanitized labels/metadata, that the existing backend belongs to
Compose project `hockeyplanner-staging`, with project directory
`/opt/hockeyplanner-staging`, base file `docker-compose.yml`, backend service and
context `backend-src`. Confirm the expected backend container name does not
collide with another project. **BLOCKED if any identity/path differs**: return
the mismatch for a reviewed implementation adjustment; do not rename a project,
move a container or adapt production configuration ad hoc.

Record `docker version`, `docker buildx version`, `docker buildx inspect` (without
`--bootstrap`), `docker compose version`, and `python3 --version` privately. Use
Docker Engine with BuildKit enabled, the Compose v2 plugin with `build.secrets`
and `config --format json`, and Python 3.12+. Local capability evidence used
Engine 23.0.5 and Compose 2.17.3; it does not prove VPS versions. Newer versions
must pass the same functional checks. **BLOCKED** for legacy `docker-compose`,
disabled/unavailable BuildKit, an untrusted remote builder, incompatible Compose
JSON or unavailable Python. Do not upgrade the VPS as part of this runbook.

Define one command wrapper for all Compose inspection/build commands:

```sh
set -eu
compose() {
  docker compose --project-name hockeyplanner-staging --project-directory /opt/hockeyplanner-staging \
    -f /opt/hockeyplanner-staging/docker-compose.yml \
    -f /opt/hockeyplanner-staging/compose.imagesharp-license.yml "$@"
}
export DOCKER_BUILDKIT=1
```

No `up`, `stop`, `rm`, restart, deploy, migration or DB command is authorized by
build-only readiness approval. Do not run the workflow's full SSH deployment script.

## 2. Owner transfers the issued license privately

The owner supplies the original issued `sixlabors.lic` file directly through an
approved encrypted channel, for example SFTP with a verified/pinned host key.
Use file transfer, not pasted shell text, `echo`, command-line values, Actions
outputs, SSH action `envs`, build args or chat. Transfer into a deploy-account
private directory outside every Git checkout and Docker build context; do not
use `/tmp`, shared folders or repository attachments. Apply `umask 077` before
creation. Never enable shell tracing or verbose/diagnostic MSBuild/binlog logging.

An authorized administrator prepares `/etc/hockeyplanner-staging/licenses` for
the existing deploy account, directory mode **0700**, then places the license
at `/etc/hockeyplanner-staging/licenses/sixlabors.lic`, owner that same account,
mode **0600**, regular file, nonempty, readable, no symlink. The ancestor directory
must prevent unauthorized users from renaming/replacing it. The license and its
parents must resolve outside `backend-src` and every other build context.
Do not add runtime secrets or mounts. Do not print or checksum the key publicly.
Confirm validity, application entitlement, expiry and renewal ownership privately.
**BLOCKED** for a sample, expired, truncated, inaccessible or permissively owned file.

## 3. Prepare the staging-local override

After explicit provisioning authorization, create only
`/opt/hockeyplanner-staging/compose.imagesharp-license.yml`, owned by the deploy
account, mode 0600, outside the backend checkout/context. Keep the base file and
its context, environment, networking, volumes, ports and other services unchanged:

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

No `args`, alternate build target, inline Dockerfile, environment-backed secret,
`services.backend.secrets` or license runtime volume. Existing backend build
arguments must be absent, since staging Release is fixed in its Dockerfile.
Secure both Compose files and their parent against untrusted writes for the
duration of preflight/build/deploy. Do not commit the local override or license.

## 4. Check exact source and resolved Compose privately

Use an approved clean checkout at the PR's **exact reviewed head SHA** containing
`Dockerfile.staging`. Before merge, staging may still be on develop. Preparing a
checkout/ref is a separate operator action requiring explicit authorization;
never substitute develop's old Dockerfile. Verify `git rev-parse HEAD`, clean
status, and the complete reviewed Dockerfile. Do not invoke `git merge`, change
running deployment refs or change a Compose context without that authorization.
If exact source cannot be made available safely, record **BLOCKED**.

Provide the existing nonsecret APP_VERSION/APP_COMMIT/APP_BUILD_TIME metadata as
the workflow does. Do not dump resolved environment configuration. Capture the
Compose JSON only in memory and validate it using the reviewed helper:

```sh
cd /opt/hockeyplanner-staging
test -f compose.imagesharp-license.yml
test -r /etc/hockeyplanner-staging/licenses/sixlabors.lic
test -s /etc/hockeyplanner-staging/licenses/sixlabors.lic
COMPOSE_CONFIG="$(compose config --format json)"
printf '%s' "$COMPOSE_CONFIG" | python3 backend-src/scripts/staging/check_imagesharp_build.py
unset COMPOSE_CONFIG
```

Only sanitized PASS/BLOCKED is emitted by the helper. **BLOCKED** on any
configuration or file-permission failure; never skip the helper, delete the
override or fall back to the shared production Dockerfile.

## 5. Prove required mounts and licensed Release without starting a container

First, under the authorized builder, use an isolated synthetic Dockerfile/context
outside checkouts. Its only RUN must have
`--mount=type=secret,id=sixlabors_license,required=true` and a file-existence check.
Without `--secret`, an uncached build must fail explicitly on the missing mount;
with a synthetic file it must succeed. No real license is needed for this probe.
If BuildKit ignores the mount or the absent-secret build succeeds, **BLOCKED**.

Then build the actual reviewed staging Dockerfile, using a separate image tag
that cannot replace the running service tag. This consumes the private file via
its path only. Set `TASK_SHA` to the verified public commit SHA; no key in args:

```sh
docker buildx build --load --no-cache \
  --secret id=sixlabors_license,src=/etc/hockeyplanner-staging/licenses/sixlabors.lic \
  -f /opt/hockeyplanner-staging/backend-src/HockeyPlanner.Backend.WebAPI/Dockerfile.staging \
  -t "hockeyplanner-staging-license-check:$TASK_SHA" \
  /opt/hockeyplanner-staging/backend-src
```

Require both `dotnet build -c Release` and `dotnet publish -c Release` to finish
successfully with ImageSharp **4.1.2**, without an invalid/expired/missing license
diagnostic. No runtime container is created or started. Preserve the previous
service image/tag. A cache hit is insufficient evidence: BuildKit secret contents
do not invalidate cached instructions; renewal also requires `--no-cache`.
The workflow additionally performs uncached **Compose** build before `up` on an
authorized post-merge deploy. Standalone build plus resolved-config validation
does not claim that post-merge deploy/smoke has already happened.

## 6. Inspect image layers and published files privately

Use `umask 077` and a private directory outside checkouts/contexts. Save the check
image with `docker image save -o <private-image.tar> <check-tag>`; this does not
start containers. Privately inspect its configuration/history and **every** saved
filesystem layer, including deleted/whiteout files, plus published DLL/deps files.
Require ImageSharp 4.1.2 in the published deps metadata; no `sixlabors.lic` or
secret mount file in any layer; no license ARG/ENV/COPY or runtime mount in image
configuration/history. A `SixLaborsLicenseFile=/run/secrets/sixlabors_license`
build instruction is a nonsecret path, not a key disclosure.

Run the reviewed offline scanner with private filenames only:

```sh
python3 /opt/hockeyplanner-staging/backend-src/scripts/staging/check_imagesharp_image.py \
  --image-tar <private-image.tar> \
  --license-file /etc/hockeyplanner-staging/licenses/sixlabors.lic
```

It reads the private license in memory and checks every config/layer file for the
full text, lines/payload tokens of at least 32 characters, and JSON/UTF-16 encodings.
It rejects secret filenames, runtime license environment and a published version
other than 4.1.2. This is byte-pattern evidence, not proof against arbitrary
encryption/obfuscation; complete source/history review remains required.
Record only PASS/BLOCKED, layer count and the public image digest;
never matched bytes, license text or resolved Compose environment. Inspect
published assemblies as bytes too. Do not rely on final visible filesystem or a
Docker history-only check: deleted files can remain in earlier layers. Keep
exports/logs private; do not upload them as GitHub artifacts. If the scanner is
unavailable, incomplete or detects a match, record **BLOCKED**, quarantine the
check image privately and return for remediation. Remove private exports through
the approved operator procedure after recording sanitized evidence.

## 7. Readiness record and PASS/BLOCKED

Record the exact source SHA, unchanged existing project/container identity,
Docker/BuildKit/Compose/Python versions, sanitized required-mount probe result,
fresh licensed build/publish result, image digest and complete layer scan result.
Record the owner and privately tracked expiry/renewal procedure without key data.

**PASS** requires all seven stages, zero runtime/DB changes, independent APPROVE
for the same head and green Quality/Security CI on that head. **BLOCKED** if any
prerequisite is missing, identity differs, build/scan is unverified, license is
invalid or review/CI covers an older head. Human alone authorizes merge. After
merge, normal develop validation → staging deploy → HP-75 smoke remain required;
they need their normal authorization and completion evidence.

Implementation-session evidence: deterministic local guards and shell regressions
were tested; local synthetic BuildKit capability/layer checks used no real key.
**Not checked on VPS:** identity, versions, ownership, license, override, exact
checkout, actual licensed Docker image/layers, expiry/renewal and build-only PASS.
These remain future operator prerequisites, not inferred from green GitHub Release.

References: [BuildKit secrets](https://docs.docker.com/build/building/secrets/),
[Compose build secrets](https://docs.docker.com/reference/compose-file/build/#secrets),
[build cache invalidation](https://docs.docker.com/build/cache/invalidation/#build-secrets),
[Six Labors build integration](https://docs.sixlabors.com/articles/imagesharp/index.html).
