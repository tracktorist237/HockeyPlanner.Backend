# HP-77 security and quality audits

This is a separate deterministic layer; the HP-71 required quality gate is unchanged.
No dependency upgrades, automatic fix PRs, AI calls, deployment or DB access occur.

## Scanners and local reproduction

- Backend: .NET 10 `dotnet package list --project HockeyPlanner.Backend.sln
  --no-restore --vulnerable --include-transitive --format json --output-version 1`
  after restore with `NuGetAudit=true`, `NuGetAuditMode=all`. Syntax verified
  against installed SDK 10.0.400 help. Includes direct and transitive advisories.
- Frontend: `npm audit --package-lock-only --json --ignore-scripts` (npm JSON v2).
  `npm ci --ignore-scripts --no-audit --no-fund` installs the exact lock tree;
  `node -e "require('browserslist')()"` detects the actual stale-data warning
  without compiling or modifying package files. HP-77 initial build also confirmed it.
- CodeQL v4: C# / JavaScript-TypeScript, `build-mode: none`, native Security tab.
  C# no-build analysis avoids executing build scripts; generated code and runtime
  behavior are outside that mode's coverage. Existing runtime/security tests remain.
- GitHub first-party secret scanning and push protection are enabled (API confirmed).
  Do not add a second scanner or fetch secret-bearing alert API payloads in CI.

Run in this repository (frontend needs the lock-installed node_modules):

```text
python scripts/quality/security_audit.py --kind backend
python -m unittest discover -s scripts/quality -p "test_*.py"
```

The wrapper captures raw subprocess output in memory and only writes sanitized
`security-audit/findings.json` and `summary.md`. A failed/malformed/incomplete scan
returns exit 2 and `SCAN_ERROR`; it never means zero vulnerabilities. A completed
scan with new high/critical returns exit 1; other completed scans return 0.
A failure before the wrapper starts fails the Actions job and may have no artifact.
NuGet restore warnings become explicit normalized advisories instead of log noise.

## Format, baseline and failure policy

Each finding has schemaVersion, source, repo, category, severity, fingerprint,
title, package/rule, version, scope, safe advisory reference, status and firstSeen.
Severity is critical/high/medium/low/info (npm moderate maps to medium).
Fingerprint is SHA-256 of repo + source/ecosystem + category + component +
advisory/rule + exact installed version + scope + advisory range/own severity
(where supplied) + effective severity. It never includes a secret value.
NuGet scope includes project basename and framework, independent of checkout path.
Npm resolves meta-vulnerability chains to their concrete advisories and exact
lockfile versions; distinct affected wrappers/versions are deliberately distinct.
Thus normalized counts differ from npm's package-level vulnerability counts.

`.security/baseline.json` is the sanitized **pre-implementation** snapshot at
`5c525b448cfad04719c4c43171683fdbe88ae3af`, observed 2026-09-30. Counts: 0 critical, 15 high, 0 medium, 6 low, 0 info.
It has exact fingerprints, category, firstSeen, status and rationale. No wildcard
or package-wide acceptance. These are deferred existing risks, not fixed issues.
No baseline is permitted for secrets. The implementation creates no follow-up issues.

- Matching baseline: **KNOWN/BASELINED**, always visible, does not block.
- Unmatched: **NEW**; high/critical fails both PR and scheduled dependency jobs.
- New medium/low/info remains visible and advisory.
- firstSeen is the observed baseline date, not an invented advisory publication date.
  NEW has null firstSeen; the run timestamp is its observation. Missing old
  fingerprints are not reported as active; baseline removal is an explicit review.
- A changed package version, severity, advisory, advisory range or project scope
  creates a new fingerprint. No workflow regenerates or broadens the baseline.
- Baseline edits are security-sensitive code changes requiring HP-76 independent
  review. The initial HP-77 PR uses the proposed pre-change baseline to bootstrap;
  subsequent acceptance needs concrete evidence and rationale, never a blanket fix.

**Native CodeQL policy differs intentionally:** findings live in GitHub's persistent
code-scanning alerts (native dedup and PR annotations), not in the dependency
baseline. The CodeQL job fails on execution/upload failure; its success alone is
NOT proof of no high findings. Native introduced-alert severity enforcement needs
an operator code-scanning rule; no settings are silently changed. Inspect the
initial CodeQL report before approving HP-77. Do not pretend legacy CodeQL findings
were observed locally or accepted into the dependency baseline. GitHub secret
alerts likewise stay native and cannot be accepted through this baseline.

## Workflow and trust

`.github/workflows/security-audit.yml`: PR to develop, push to develop, Monday 07:23 UTC,
workflow_dispatch. PR scans its merge SHA; other events require the develop ref
and explicitly check out develop. The repository guard excludes forks' own schedules.
Develop pushes also establish the native CodeQL base analysis after merge, even
while weekly activation awaits the default-branch decision. Before that first base
analysis, native PR comparison may be unavailable; inspect the initial full scan.
No `pull_request_target`, environments, repository secrets, SSH, deployment, DB,
commits, settings writes or dependency update steps. Fork PRs get no trusted secrets.
Contents is read-only; only the CodeQL job gets security-events:write for native
SARIF upload. Default GITHUB_TOKEN only. Checkout never persists credentials.
No raw scan logs or raw SARIF are uploaded as generic artifacts. The normalized
metadata artifact expires after seven days. Inspect source only with sanitized
metadata; neither AI nor public reports should ingest secret alert values.

Action audit: first-party actions only, immutable reviewed tag SHAs: checkout v6,
setup-python v6, setup-node v6 / setup-dotnet v5, upload-artifact v4, CodeQL v4.
These use the hosted ubuntu-latest runner; no third-party action or API-key service.
Pins were resolved from the upstream GitHub tag refs on 2026-09-30. Pin updates need
review; existing deployment/validation workflow action dependencies were not changed.

## Observed GitHub settings and operator prerequisites

Read-only authenticated API observation, 2026-09-30, both repos:

- Public; default branch **master**. Secret scanning and push protection enabled.
- CodeQL default setup `not-configured`; therefore the advanced workflow is not a duplicate.
- Dependabot config absent. Automated security updates disabled (no auto-upgrade PRs).
- Dependency graph SBOM and vulnerability-alerts endpoints returned HTTP 404.
  Their actual enablement is **unconfirmed**, not inferred from public visibility.
- Actions enabled, all actions allowed, SHA pin requirement false; default token
  permissions read and PR approval permission false. New actions are pinned anyway.
- Develop protection: strict required check `validation / Backend quality gate`,
  enforce admins true. No branch protection modifications were performed.

**READY FOR OPERATOR SETTINGS.** These are prerequisites/recommendations, not applied changes:

1. GitHub runs schedule only when the workflow exists on the default branch.
   With current master default, merging only to develop **does not activate cron
   or guarantee workflow_dispatch availability**. Master is out of scope.
   After human review/merge, Sergey must decide whether to change default branch:
   repository Settings -> General -> Default branch -> switch to develop -> Update.
   Assess repository-wide effects first; do not perform this automatically.
   If master must remain default, weekly activation is blocked pending a separately
   approved trusted scheduler/default-branch arrangement. Local scans and PR scans work.
2. Settings -> Security -> Advanced Security (older UI: Code security and analysis):
   verify Dependency graph and Dependabot alerts; enable if desired/available.
   Keep Dependabot security updates and version-update PRs disabled for HP-77.
   Verify Secret Protection -> Secret scanning and Push protection are enabled;
   Security -> Secret scanning shows native alert status. Do not export values.
3. Code Security -> Code scanning -> confirm advanced CodeQL analysis appears after
   the PR workflow. Do not enable default setup alongside this workflow.
   Security -> Code scanning -> filter branch develop after merge to verify coverage.
   If feature/settings are unavailable, record the UI limitation; do not claim enablement.
4. Optional: Settings -> Rules -> Rulesets -> the existing develop-only rule
   (or a separately reviewed develop rule) -> Require code scanning results ->
   CodeQL -> security alert threshold High or higher. Preserve existing protection;
   do not add duplicate/conflicting rules. This native introduced-alert enforcement
   is distinct from simply requiring the CodeQL execution check.
5. After observing a real run, consider requiring **Deterministic dependency audit**
   in the existing develop protection's required status checks (GitHub Actions).
   Existing required context stays `validation / Backend quality gate`. Check the
   actual PR check context before selecting; no guessed or fabricated context.
6. After schedule activation, Actions -> Security audit -> Run workflow -> develop.
   Verify both jobs, Job Summary, artifact contents/retention and first weekly run.
   Scheduled execution has not been proven by a PR run.

## AI separation, privacy and rollback

AI is a separate external ChatGPT automation, created by Sergey **after merge**
with connected GitHub + Linear. No OpenAI/Codex API key or AI Action is introduced.
See [AI audit contract](ai-security-audit.md) for the canonical read-only contract, evidence and dedup rules.
`secret_metadata()` is an operator-side allowlist example with a synthetic GitHub
alert regression test: only alert number/rule are retained; secret, URL, path,
prose and unknown fields are dropped. CI does not call the secret alerts API.
This is a sanitizer test, not a claim that a real secret was uploaded to test GitHub.

Disable: Actions -> Security audit -> menu -> Disable workflow (operator), or
revert only the HP-77 task changes through a reviewed PR. Do not disable HP-71,
change baseline to hide failures, alter production workflows or touch VERSION.
If made required, the operator must coordinate removing just the HP-77 requirement
before disabling it. Disable the future external AI automation separately.

References: [schedule semantics](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule),
[CodeQL action](https://github.com/github/codeql-action),
[C# no-build coverage](https://docs.github.com/en/code-security/code-scanning/creating-an-advanced-setup-for-code-scanning/codeql-code-scanning-for-compiled-languages).
