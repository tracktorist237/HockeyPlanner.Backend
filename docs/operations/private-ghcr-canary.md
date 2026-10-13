# HP-91: private GHCR canary preparation

Status: **implementation only; publication has NOT been performed**.
[HP-91](https://linear.app/hockeyplanner/issue/HP-91/security-one-time-private-ghcr-canary-with-digest-verification)
is stacked on PR #23, base `quality/ghcr-artifact-validation` at
`24097e58cff2ed104682be83bed3673d93ec5e8a`; PR #22 remains at
`f8a027094e39ec7c1f9dc1c49ed61384f895986e`. The owner authorized this temporary
stacked process exception. Both parent PRs/branches remain unchanged.

## Operator boundary — do not apply the label during implementation

Only repository owner `tracktorist237` may apply **ghcr-canary-approved**, once,
to the open same-repository PR from `quality/ghcr-private-canary` targeting the
exact parent above. Do this only after a NEW independent read-only session gives
APPROVE for final exact head, all mandatory Quality/Security/CodeQL/local-artifact
CI is green, and live base/head/merge parents still match the reviewed report.
Previous parent approvals do not cover this code. No author approval or merge.

Before labeling, the owner must privately inspect their complete package namespace
(including private packages) and confirm `hockeyplanner-backend-hp91-canary` has
NEVER existed, is not a deleted/restorable package, and has no tags/versions.
Ensure no other writer/admin/automation can create, restore or mutate this reserved
name while the transaction runs. Apply the label only when explicitly authorizing
one private linux/amd64 canary at `ghcr.io/tracktorist237/hockeyplanner-backend-hp91-canary`,
tag `canary-<reviewed full head SHA>`. No latest, staging/production image or release.
Namespace ownership remains a personal account; transfers/organization semantics
are unsupported. Confirm GITHUB_TOKEN package creation/read/write is permitted
and the Packages API/registry distinguish absence from inaccessible resources.
If any ownership/absence/permission assumption cannot be established, **BLOCKED:
do not label**. Implementation's metadata-only404 is NOT namespace-absence proof.

## Enforced transaction and one attempt

The publication workflow listens only to pull_request/labeled on the parent base.
No push/opened/synchronize/merge/schedule/release/manual-dispatch trigger exists.
Its job requires the exact head branch, same repository, owner actor AND triggering
actor, approval label and run_attempt=1. One constant concurrency group serializes
all canary attempts without cancellation. Python independently enforces the same
event boundary, validates live head/base/merge, Git parents, label still present
and exact-head successful GitHub Actions checks. Public GitHub read endpoints
avoid granting additional Actions/PR write permissions; API errors/rate limits block.

Exactly one approval-label event in PR history is allowed. Full bounded workflow
run history must contain no earlier non-skipped publication job: failure,
cancellation, pending state or success all consume the one attempt. Rerun and
remove/reapply-label are BLOCKED. Incomplete/oversized/unavailable history blocks.
Skipped jobs for unrelated labels do not publish or consume an attempt. After
failure, do not delete/relabel/rerun to defeat this protocol. Any exceptional
recovery requires a separately authorized, reviewed change; no retry bypass exists.

The prepare step supplies only the existing SIXLABORS_LICENSE_KEY and calls the
unchanged HP-90 helper: required BuildKit file secret, uncached --load build,
exact local IID inspection/export and real scanner. DLL pin, deps4.1.2, historical
leak scan, config/DiffID identity and rootfs checks remain unchanged. License and
archives are removed before the next step; a private readonly runner-temp JSON
record carries ONLY merge SHA/IID/layer count/PASS. It cannot authorize a different
source snapshot or image. No rebuild occurs between scan and push.

Publication alone gets GHCR_JOB_TOKEN from github.token, with job permissions
contents:read/packages:write. It revalidates source/history/proof, checks Package
API absence AND authenticated registry tags/list returning typed NAME_UNKNOWN;
REST404 alone, existing/empty packages, denied access, malformed/opaque errors
and unsupported responses block. The supported model trusts registry error
semantics and GitHub's write-implies-metadata-read permission model, plus the
owner's exclusive namespace reservation above. There is no registry atomic
create-if-absent guarantee against an out-of-band administrator/writer.

Only then Docker login uses password-stdin and isolated0700 temporary config
(config.json0600). Token values never enter CLI args, build environment or logs.
The exact scanned IID is tagged locally; its identity is rechecked, then source
and absence are checked immediately before the sole push. Only that tag is pushed.
Step-scoped secrets are not a sandbox: same-repository maintainers editing build
code/workflows and owner-approved code are trusted. The job token permission is
job-scoped and remains available to Actions through github.token; it is not a
claim that untrusted same-repository code is isolated by shell environment scoping.

## Mandatory post-push proof and failure semantics

Push output supplies the registry manifest digest. Authenticated GET by tag AND
digest must have matching Docker-Content-Digest and actual manifest-byte SHA256,
single-platform supported manifest type, and config.digest equal to scanned IID.
Authenticated Docker pull uses `image@digest`; inspection must match config IID
and RepoDigests. Registry manifest digest and config IID are different identities.
Packages API independently requires private visibility, expected owner/repository,
one version with the matching digest and exactly the SHA-specific tag.

Anonymous verification performs the unauthenticated token exchange and, if a
token is issued, requests the actual immutable manifest. Only401/403 with typed
DENIED/UNAUTHORIZED errors passes; rate limits and opaque errors block.
A plain registry challenge401,404, transport error or inability to inspect privacy
is not sufficient. A public manifest or any identity/privacy mismatch blocks PASS.
No automatic visibility change, delete, rollback or repush is performed.

After a push attempt, BLOCKED may mean the private package already exists. Obtain
separate operator inspection; do not claim rollback, absence or retry safety.
Sanitized success summary contains package URL, source/merge SHA, config IID,
registry digest, immutable reference, PRIVATE and anonymous DENIED only. Failure
logs generic BLOCKED requesting prerequisite inspection. No raw diagnostics,
archives, license, authentication config or build records are uploaded. Ordinary
cleanup removes private proof/config; abrupt cancellation relies on disposable
hosted VM destruction. Self-hosted/shared runners are unsupported.

## Preparation evidence and deferred scope

Ordinary stacked PR CI extends only the necessary base filters/conditions and
executes unchanged mandatory gates plus the HP-90 non-publishing build/scan.
Synthetic tests exercise real archive reconstruction and all canary decisions;
they do not prove live GITHUB_TOKEN/registry/privacy behavior. Real publication,
private visibility, authenticated digest pull and anonymous denial remain
**NOT VERIFIED until separately authorized owner action after independent APPROVE**.

No staging/production/VPS/DB/frontend/VERSION/protected-branch/Linear change is
authorized. No Compose, server credentials, locks, Init, recreation, smoke or
deployment work. OPERATOR PREFLIGHT: NOT VERIFIED for staging. Canary success
never authorizes deployment or marks HP-91 Done. Disable before publication by
removing this workflow/helper through review; package cleanup after publication
needs separate authorization. ADR/roadmap/DoD/security baseline remain unchanged.

Sources: [GitHub package permissions](https://docs.github.com/en/packages/learn-github-packages/about-permissions-for-github-packages),
[private-by-default personal packages](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility),
[Packages REST API](https://docs.github.com/en/rest/packages/packages),
[registry token authorization](https://distribution.github.io/distribution/spec/auth/token/),
[registry API error/digest semantics](https://distribution.github.io/distribution/spec/api/).
