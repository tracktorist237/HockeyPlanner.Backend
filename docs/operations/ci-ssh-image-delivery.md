# HP-92 — private scanned image delivery

Implementation/Draft PR only. **No VPS access, transfer, load, deployment,
provisioning or GHCR publication is authorized by this document.**
[HP-92](https://linear.app/hockeyplanner/issue/HP-92/quality-deliver-ci-scanned-backend-image-privately-over-ssh-to-staging)
is stacked on PR #23 at `24097e58cff2ed104682be83bed3673d93ec5e8a`, targeting
`quality/ghcr-artifact-validation`. PR #24 is not an ancestor.

## Image and credential boundaries

The HP-90 helper still builds the actual staging Dockerfile once, uncached,
with a private BuildKit file secret, checks its local config IID, exports that
IID once and runs the complete historical/license/DLL/deps/rootfs scanner.
The delivery callback additionally binds the Docker manifest to the single
tagless OCI graph when present. Aliased outer paths, duplicate JSON keys,
multiple manifests, reference annotations, compressed source layers, unsupported
graphs and missing/mismatched blobs block before transfer/load. Archive SHA256
is computed over the very bytes that passed these checks. There is no second build.

The license file and builder are removed before the callback. One hosted-job
build step preserves the same archive bytes in an exclusive runner-temp0700
directory, archive0400 and verification JSON0600. A second step gets only the
existing VPS_HOST/VPS_USER/VPS_SSH_KEY/STAGING_KNOWN_HOSTS mechanisms; no license.
Its SHA256/size/source checks reject altered or incomplete local artifacts.
No archive/config/license/build log is uploaded or published. Ordinary successful
delivery deletes the local archive; interrupted/failed hosted jobs rely on disposable
VM destruction for residual private inputs. Shared/self-hosted runners are unsupported.

The transport removes credentials from child environments, uses ephemeral0700/
0600 SSH files, disables inherited SSH configuration/agents/proxies/forwarding,
requires an existing trusted pin for the exact VPS_HOST and uses
StrictHostKeyChecking=yes. No discovery/keyscan/accept-new fallback is provided.
The existing smoke pin must actually cover the deployment endpoint; the owner
must verify that binding through a trusted channel. This implementation inspected
secret **names only**, not values, and made no environment/secret/settings changes.

Only `/usr/bin/python3` and the fixed reviewed receiver path are invoked remotely,
with validated public source SHA/config IID/archive SHA/size arguments. The archive
travels encrypted on stdin, without a user-selected remote destination or command.
The receiver opens no payload file until its read-only locked preflight succeeds.
Private receipt uses exclusive files, bounded size, exact EOF, SHA256 and fsync;
truncation, extra bytes, collisions or hash mismatch cannot authorize Docker load.

## Docker29 content identity

Docker29/containerd .Id is a manifest/index digest, whereas HP-90's source IID is
the image configuration hash. They are deliberately different identities.
Moreover, containerd's legacy Docker-save importer can reuse compressed blobs
already labelled with the same DiffID. The imported manifest can differ between
empty and populated stores despite identical source archive bytes.

The receiver therefore never predicts or equates these IDs. It accepts exactly
one tagless load result, re-exports that immutable observed ID and verifies the
single OCI manifest's raw digest, raw configuration SHA against the scanned source,
all descriptor sizes/blob hashes and every ordered uncompressed layer DiffID.
Only gzip and uncompressed export layers are admitted; source layers remain
uncompressed as required by the historical scanner. Unknown/multi-platform/index
models block. Rootfs/mount proof uses the same verified source archive and existing
scanner reconstruction/DLL/deps logic without sending license patterns to the VPS.
The historical licensed scan remains mandatory on CI; the existing scanner CLI
and staging-local build helper have no new bypass option.

Only the resulting content-bound manifest ID replaces image in the frozen private
Compose snapshot. Before up, all source/configuration/approval/container/lock/store/
mount guards are rechecked. `up --no-build --pull never backend` and postconditions
bind both container Image and Config.Image to that ID, along with existing runtime,
project, working-directory, dependency and actual mount checks. No image tags are
loaded/replaced, existing backend remains running until verified replacement, and
old images are not pruned. No automatic rollback or daemon atomicity is asserted.

The CI-only licensed build/scan workflow now loads those same scanned bytes into
two disposable, network-disabled Docker29.1.3/containerd2.2.0/runc1.3.4 daemons,
one empty and one actually seeded/unpacked with a compressed matching first layer.
It uses checksum-pinned Compose2.40.3 and creates, **never starts**, a container
to verify selection and explicit Init=false. The pinned dind image and Compose
binary are external test tools only, never staging installation instructions.
No SSH/staging secret/environment is available to this PR job.

Local actual-daemon synthetic evidence showed the SAME archive/config/DiffID
imported as `ca08f4db9371dcc860cf3a573d5ea6f094c5794cef30af856ceed6c73f13a072`
on an empty store and `b0f0cbfa1fe57c68df246244e761f6b1d51128c12051fa2f208815176636ca25`
after gzip prepopulation. Compose2.40.3 create selected the content-bound ID.
This is import mechanism evidence, not a licensed local build or VPS readiness.

## Operator prerequisites — not provisioned or verified

The develop workflow still requires validation before deploy and successful deploy
before HP-75 smoke. Task pushes/PRs cannot start it. Its FIRST step requires an
owner-configured HP92_APPROVED_HEAD variable matching the exact develop source SHA;
the existing protected staging-smoke environment also requires owner review.
Neither approval is configured by this implementation. Approval never bypasses
receiver preflight or permits unreviewed source. Never set it merely to make CI green.

After separate authorization, the owner must prepare/review the exact clean backend
checkout (this receiver does no fetch/merge/reset), existing base configuration and
private0600 `compose.ci-image.yml`. The resolved backend/dependency closure must
contain no build, runtime license, unknown mounts, or inferred init setting;
APP_COMMIT must equal the exact source SHA. No frontend checkout/version read is
performed by this delivery path. Native configuration round-trip must pass.
The old staging license file must be absent; no license is provisioned/transferred.

Keep the SAME deploy-owned0700 directory and stable0600 lock inode from
[the reviewed lock/recovery protocol](imagesharp-staging-operator.md). No missing
lock, existing barrier, unsafe ownership, Init=null, unknown runtime/store, conflicting
backend or noncooperating writer can be reconciled automatically. Admission requires
the protected0600 `hp92-operator-approval.json` record:

```json
{
  "protocol": "hp92-flock-recovery-v1",
  "sourceSha": "<exact authorized 40hex source SHA>",
  "allWritersCooperate": true,
  "frontendCooperates": true,
  "ownerApprovedTransferLoadDeploy": true
}
```

This is an owner attestation following actual inspection, **not proof generated
by this program**. Every permitted writer must already obey this lock/barrier
protocol, including frontend. Unrestricted/noncooperating privileged writers
invalidate the model. Missing/false/stale approval blocks before private receipt.
Stock trusted binaries, containerd2.2.0, ancestor/source/config ownership, actual
volume backing/mount trees, host-key binding and recovery procedures remain owner
prerequisites. No server image-store switch, build tooling, broad privileges,
frontend change or automatic license/Init/lock reconciliation is introduced.

The exclusive FD covers admission/receipt/load/Compose/postchecks. Existing supervised
POSIX commands and durable barrier cover cancellation/SSH loss/uncertain daemon
operations. Receipt cancellation and unverified import/up retain private inputs
and recovery-required. Only fully verified completion cleans private files before
removing the barrier. Killing a CLI never proves daemon quiescence. Recovery and
rollback require separately authorized operator work under the original runbook.

## Current security gate blocker and review

The 2026-10-14 local deterministic audit returned NEW_HIGH_OR_CRITICAL: 32 project-
scoped findings, 21 known/baselined and 11 NEW (7 high, 4 medium). New advisories
affect System.Security.Cryptography.Pkcs9.0.0/10.0.0 and .Xml9.0.0. Dependencies,
baseline and failure policy are unchanged; remediation is outside HP-92 and needs
separate authorization. Green parent CI from 2026-10-13 is not current-head proof.
Record exact-head CI outcomes and independent review separately; this document
does not provide independent approval. Human merge remains blocked on red CI.

Known staging blockers remain: existing Init=null, unprovisioned shared lock,
frontend nonparticipation and unverified all-writer cooperation. No staging smoke
is claimed. Revert only the task commits through review to remove this code; no
VPS/production/DB rollback is needed because no such operation occurred here.

**OPERATOR PREFLIGHT: NOT VERIFIED. HUMAN MERGE / DEPLOY: BLOCKED.**

Exact upstream identity semantics:
[Moby29 load/export](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/containerd/image_exporter.go),
[Moby29 inspect](https://github.com/moby/moby/blob/docker-v29.1.3/daemon/containerd/image_inspect.go),
[containerd2.2 importer and compressed reuse](https://github.com/containerd/containerd/blob/v2.2.0/core/images/archive/importer.go),
[Compose2.40.3](https://github.com/docker/compose/releases/tag/v2.40.3).
