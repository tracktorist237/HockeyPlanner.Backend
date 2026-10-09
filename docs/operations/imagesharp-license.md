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

## Staging is blocked pending operator approval and setup

The existing staging deployment runs `docker compose build backend` on the VPS.
Its shared Dockerfile builds and publishes Release. The GitHub runner license
does not reach that machine. Neither the VPS state nor its current Compose file
was inspected in this task. There is no evidence of a configured BuildKit license.
The current Dockerfile does not consume build secrets; without a separately
configured license, post-merge staging build will fail.

**Do not human-merge this PR until the following staging prerequisite is approved,
implemented and verified.** Green PR validation is not staging build evidence.
This task does not connect to the VPS, deploy, modify the active Dockerfile,
staging Compose, production Compose or production workflows.

`imagesharp-staging-build.proposed.patch` is a reviewable, unapplied proposal.
It adds a staging-only Dockerfile copied from the current shared Dockerfile.
Each Release build/publish RUN has a required BuildKit secret mount and passes
only the mounted path to `SixLaborsLicenseFile`. It does not use license ARG,
ENV, COPY, SSH payloads or shell-expanded license command arguments. Production
continues using its existing Dockerfile.

### Operator runbook (requires a separate owner authorization)

1. Review the proposal and actual staging Compose service build context,
   Dockerfile and deployment account. Approve a separate implementation of the
   staging-only Dockerfile and the staging caller changes below. Do not apply
   the patch to production or alter `infra/docker-compose.yml`.
2. Verify the staging Docker Engine supports BuildKit and the installed Compose
   plugin supports `build.secrets`. Verify `docker buildx version` and
   `docker compose version`. Local Engine 23 capability testing is not proof
   that the VPS has the same versions.
3. The owner provisions the valid full license through an approved private
   channel directly on staging, outside both Git checkouts and every build
   context, at `/etc/hockeyplanner-staging/licenses/sixlabors.lic`.
   Restrict the directory to the authorized deploy account and file mode 0600.
   Never pass the license through the SSH action script, command-line literals,
   build args or logs. Do not use the expired upstream sample or a fabricated key.
4. Create a staging-local override (not production configuration) at
   `/opt/hockeyplanner-staging/compose.imagesharp-license.yml`:

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

   Keep the current service build context unchanged; the Dockerfile path above
   is relative to that context. Verify the context points to backend-src. This is
   a build secret only: do not add `services.backend.secrets` or a runtime volume.
5. In a separately approved staging-workflow change, select both the current
   staging Compose file and this override for the existing build/up/ps commands.
   Assuming the current file is `/opt/hockeyplanner-staging/docker-compose.yml`,
   the build command becomes:

   ```sh
   docker compose -f docker-compose.yml -f compose.imagesharp-license.yml build backend
   ```

   Retain the existing APP metadata, exact-SHA checks, validation dependency,
   serialization and smoke dependency. First confirm the actual filename and
   project identity on staging; do not change the Compose project name or other
   services. No GitHub license value belongs in the remote script.
6. Validate Compose syntax using `config --quiet` (never dump resolved environment
   configuration). Test an isolated authorized build without the secret and
   require failure before compiler execution. With a valid mounted file, require
   Release build/publish success at the approved exact SHA. Do not run `up` as
   part of read-only preflight; deployment needs its own authorization.
7. Verify the published application and exported image layers contain no license
   file or value using a private local check that outputs only pass/fail. Do not
   upload license-bearing logs, cached filesystem dumps or image exports as
   artifacts. Inspect Docker history for absence of license ARG/ENV/COPY; commands
   may include the non-secret mount path. Synthetic local layer tests alone do not
   verify the real licensed staging image.
8. Record readiness, exact SHA, Docker/Compose versions, license expiry/renewal
   owner and sanitized build results. Only then allow human merge after separate
   review and all CI gates. After merge, follow normal develop validation, staging
   deployment and HP-75 smoke. HP-84 remains Draft/BLOCKED until its own process.

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
