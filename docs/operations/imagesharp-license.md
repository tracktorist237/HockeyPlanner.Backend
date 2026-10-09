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

The owner authorized staging-only implementation in Draft PR #22. The staging
workflow now selects project `hockeyplanner-staging`, the existing base file
`/opt/hockeyplanner-staging/docker-compose.yml` and a private local override
`/opt/hockeyplanner-staging/compose.imagesharp-license.yml` for config/build/up/ps.
The operator must confirm that this is the existing project and base filename;
the actual VPS state has not been inspected. No remote changes are authorized.

`Dockerfile.staging` fixes both build and publish to Release with required
`sixlabors_license` BuildKit mounts and `SixLaborsLicenseFile` paths. It accepts
no build arguments. The shared production Dockerfile remains unchanged.
The workflow checks file availability and privately validates resolved Compose
JSON, including Dockerfile, context, final target, build-only secret mapping,
deploy-account ownership and 0600/0700 license permissions. Configuration is
never printed. Build uses `--no-cache` before `up --no-build`; any check/build
failure exits before container replacement. Global container-name removal was
replaced by Compose's normal update of the selected project.

The GitHub runner license is never transmitted to the VPS. There is no license
ARG/ENV/COPY, SSH payload or key-valued shell argument. Staging must receive its
private file directly from the owner through a separately authorized operator
procedure. The old unapplied proposal is superseded by the implemented file.

**Human merge remains blocked pending separately authorized operator setup and
build-only verification, plus independent exact-head review.** Green PR CI is
not evidence of a licensed staging Docker build. Follow the separate
[staging operator runbook](imagesharp-staging-operator.md); all remote checks and
provisioning in it remain planned, not performed by this implementation session.

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
