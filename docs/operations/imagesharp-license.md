# ImageSharp 4.1.2 build license and staging prerequisite

The owner confirmed that Six Labors issued a Community License for HockeyPlanner
and placed its full text in the repository secret `SIXLABORS_LICENSE_KEY`.
The dependency upgrade does not add a public license file.

## GitHub validation

`validate.yml` declares one required workflow-call secret. The PR caller
passes it only for a head repository equal to this repository. The staging
validation caller passes that one secret explicitly. There is no secret
inheritance and no `pull_request_target`.

Only the Release build step receives the environment variable. WebAPI maps it
to the supported MSBuild `SixLaborsLicenseKey` property inside the project;
the shell never expands the value into command arguments. Restore, tests,
contract checks, security audit, CodeQL, uploads and staging SSH receive no
license environment variable. No binary MSBuild logs are generated or uploaded.

If the secret is unavailable (including fork PRs), mandatory validation still
runs and Release fails before invoking the build. This is intentional and does
not waive the quality gate. Do not enable secrets for fork code to avoid that
failure. Same-repository contributors who can modify build code must be trusted
to use the licensed build; step scoping is not a sandbox for malicious MSBuild.

## Local development

A developer can privately supply the full license via their local environment,
or use `SixLaborsLicenseFile` to reference a private file. Do not paste the key
into a terminal command, Git, chat or diagnostics. The filename `sixlabors.lic`
is ignored by Git and Docker contexts; binary logs are excluded from Docker too.
Do not use verbose/diagnostic build logging, shell tracing or binlogs while a
license is present. Debug may compile with a license warning, but that does not
prove Release readiness. A GitHub secret must never be extracted for local use.

## Staging build integration and remaining operator prerequisite

The staging transaction is serialized by the shared host lock
`/var/lib/hockeyplanner-staging/deploy/deploy.lock`, including all permitted
operator writers. It validates project `hockeyplanner-staging`, base and private
override under `/opt/hockeyplanner-staging`, then freezes resolved configuration
in a private mode0400 snapshot and exact commit source in a read-only Git archive.
BuildKit consumes the required file secret for fixed Release build/publish;
its IID file binds the result independently of mutable tags. The only controlled
snapshot change after build is image=verified fullSHA256 IID. Config/source/image/
lock/container drift fails before up; existing backend identity is checked immediately
before replacement. Validation → deploy → smoke and CI secret boundary are preserved.

Lock alone is insufficient after process/SSH loss. Every writer must also reject
`recovery-required` after acquiring the lock, before any mutation. Supervised POSIX
commands inherit the FD and use a separate process group; cancellation/timeouts
terminate the group and reap the direct child. A durable barrier retains uncertain
Docker outcomes and private inputs until separately approved operator recovery.
Docker daemon work is not made atomic by killing its CLI. No automatic recovery
or skip/scan-bypass flag is provided.

Both normal deploy and build-only export their CURRENT builder IID to a private
archive and invoke the existing historical/final-filesystem scanner with that IID,
including the pinned official4.1.2 DLL and layer/config identities. An earlier
build-only image never authorizes a later build. Scan/export failure prevents up.

`Dockerfile.staging` has no license ARG/ENV/COPY. Production Dockerfile/Compose/
deploy remain unchanged. GitHub license is never sent through SSH: staging needs
owner-provisioned private0600 file/0700 parent outside all contexts. Runtime file
secret aliases (including dependencies, canonical paths and hard links) cannot
expose that license; unsupported/ambiguous sources fail closed.

Named volume definitions require a supported local driver and matching actual
Docker-volume inspection. Bind-backed devices and actual Mountpoints are checked
by a bounded metadata-only inode walk. Unreadable/oversized/symlink/special trees,
external/unknown volumes and volumes_from are BLOCKED; no blanket secret prohibition.
All effective dependency mount targets and image-declared volumes must preserve
`/app`, the DLL and WebAPI deps. Post-up actual IID/labels/mount bindings are checked;
an unexpected result retains the recovery barrier for an operator, without claiming
the unsafe runtime was prevented atomically. Actual VPS compatibility is unverified.

**Human merge remains blocked pending separately authorized operator setup/PASS
and new exact-head independent review.** Follow the
[operator runbook](imagesharp-staging-operator.md) for the shared lock, source/config/
IID binding, build-only mode, historical leakage and final filesystem/trusted-DLL
verification, renewal and rollback. All actual VPS prerequisites are NOT VERIFIED;
no remote files, license transfers, containers or deployments were changed here.

### Renewal and rollback

Record actual expiry privately; arrange renewal before CI/staging Release stops.
BuildKit secret contents do not invalidate a cached RUN automatically: after
renewal verify an uncached licensed build instead of treating a cache hit as
fresh license evidence.

For an approved future rollout, keep the previous staging image and restore it
through the normal operator deployment procedure if necessary. No migration is
introduced. Reverting the package to 3.1.12 restores the known vulnerabilities;
never suppress the audit or broaden the baseline to make rollback look secure.
Rollback of this task before merge requires only reverting its task-branch
commits; no remote infrastructure was changed.

Official references:
[Six Labors build integration](https://docs.sixlabors.com/articles/imagesharp/index.html),
[Community License](https://licensing.sixlabors.com/),
[Docker BuildKit secrets](https://docs.docker.com/build/building/secrets/),
[Compose build secrets](https://docs.docker.com/reference/compose-file/build/#secrets).
