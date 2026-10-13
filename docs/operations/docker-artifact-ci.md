# HP-90: CI-only licensed Docker artifact validation

[HP-90](https://linear.app/hockeyplanner/issue/HP-90/quality-validate-licensed-docker-artifact-in-ci-before-ghcr-publishing)
is intentionally stacked on [PR #22](https://github.com/tracktorist237/HockeyPlanner.Backend/pull/22),
base `quality/security-imagesharp-remediation` at
`f8a027094e39ec7c1f9dc1c49ed61384f895986e`. The owner explicitly authorized this
temporary departure from the normal develop-targeted process. It validates a
local artifact; it does not publish to GHCR or prove staging readiness.

## Executed CI chain

The pull-request-only workflow runs on a disposable hosted Ubuntu runner with
contents:read. Checkout uses the PR synthetic merge, without persisted credentials.
The helper checks HEAD against GITHUB_SHA. Existing Quality and Security workflows
also accept this one stacked PR base; their develop/master behavior and all gates
are retained. Forks run mandatory checks but receive an empty license and fail.

Only the build/scan step gets the same-repository SIXLABORS_LICENSE_KEY. Python
removes it from the subprocess environment, writes a mode0600 file in a mode0700
runner-temp directory outside checkout, and gives BuildKit only its file path.
The existing staging Dockerfile requires `sixlabors_license` at both Release
build and publish RUNs. No ARG/ENV/COPY secret, value-valued shell argument,
registry credentials, login action or inherited secrets are introduced.
Same-repository maintainers able to edit MSBuild, Dockerfile, helper or workflows
are trusted: step scoping and BuildKit secret mounts do not sandbox malicious
authorized build code.

The helper creates an isolated docker-container Buildx builder with an empty
private Docker config. It builds linux/amd64 using `--load --no-cache --pull`,
without tags, cache import/export, registry output or push. Provenance/SBOM
export is disabled for this single local Docker output; no build record is
uploaded. Dependencies/base images are downloaded normally, not published.
Docker documents [local output](https://docs.docker.com/reference/cli/docker/buildx/build/#load)
and [BuildKit file secrets](https://docs.docker.com/build/building/secrets/).

The builder IID must be full sha256 and equal the loaded Docker inspection ID.
Docker save exports that exact IID, never a mutable tag. The existing offline
scanner verifies the exported config hash against it, every layer DiffID,
historical license disclosure, final deps and official ImageSharp4.1.2 DLL pin.
The scanner's existing rootfs checks remain active. Missing/mismatched IID,
export errors, corrupt/unsupported archives, scanner rejection or exception
fail the job. No scan bypass exists. Unsupported manifest/index-IID or compressed
archive models fail closed; this check does not adapt the staging runtime profile.

Only commit SHA, local config IID, scanned layer count and PASS leave the helper.
Failure emits generic BLOCKED, suppressing raw subprocess/scanner diagnostics.
Local IID is **not a GHCR registry digest**. Archives/license/private config are
deleted on ordinary success/failure, and builder removal is mandatory. Abrupt
runner cancellation may interrupt cleanup; disposable hosted VM destruction is
the remaining boundary. No self-hosted/shared runner is supported. No artifacts
are uploaded by this workflow, including raw archives or build records.

## Scope, deferred work and rollback

API, application behavior, database, production configuration, VERSION, frontend,
parent PR/branch and staging infrastructure are unchanged. No deployment helper,
Compose up, SSH, smoke or rollback operation is invoked. Deterministic tests use
synthetic archives/licenses; only authorized hosted CI performs the real build.

Registry publishing/credentials/packages:write/private visibility, digest-based
deployment, VPS credentials, Compose changes, Init reconciliation, shared writer
lock adoption, recreation and exceptional recovery remain separate authorized
work. **OPERATOR PREFLIGHT: NOT VERIFIED.** The parent runbook's operator blockers
are unchanged. Green CI does not authorize merge/deploy or establish issue Done.
Independent exact-head review and human decisions remain required.

Rollback is removal of this CI workflow/helper and reversal of the narrow stacked
PR filters through a reviewed PR; it has no VPS impact. Dependency baseline and
governance criteria/debt remain unchanged; no new architectural policy is selected.
