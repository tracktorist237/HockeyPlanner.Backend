# GitHub Workflow

## Labels

Use one type, one priority, one or more areas and optional workflow labels.

### Type

- `type:refactor`
- `type:test`
- `type:bug`
- `type:docs`
- `type:security`
- `type:infrastructure`
- `type:tech-debt`

### Priority

- `priority:p0` - active security/data-loss risk; blocks planned work.
- `priority:p1` - high risk; must be handled in its assigned milestone.
- `priority:p2` - meaningful but not blocking.
- `priority:p3` - low-impact or deferred improvement.

### Area

- `area:auth`, `area:users`, `area:teams`, `area:events`
- `area:attendance`, `area:roster`, `area:goalies`
- `area:notifications`, `area:files`, `area:database`
- `area:api`, `area:infrastructure`, `area:architecture`

### Workflow

- `status:ready`
- `status:blocked`
- `status:needs-adr`
- `status:needs-review`

Do not duplicate GitHub milestone information as a `milestone:*` label.

## GitHub milestones

Create exactly twelve GitHub milestones matching the approved roadmap:

`M1 Safety Tests`, `M2 JWT Identity`, `M3 - Users, Push and Notifications`,
`M4 Unified Auth Model`, `M5 Error Handling`,
`M6 Background Notifications`, `M7 Teams Controllers`,
`M8 Admin and Goalies`, `M9 External Integrations and Files`,
`M10 Production Readiness`, `M11 Dependencies and Cancellation`,
`M12 Architecture Boundaries and DTOs`.

Do not use GitHub milestones to redesign or subdivide the roadmap.

## Develop protection

Configure **only develop**. Do not modify master/release settings under this
process. The actual Actions check context is
**`validation / Backend quality gate`**, published by `github-actions` through
`backend-pr-checks.yml` calling `validate.yml`. Do not substitute the workflow
title or the retired `Backend PR Checks / Build and test` name.

Repository settings, not this document or a PR template, enforce the gate.
If admin API/CLI access is unavailable, report `READY FOR OPERATOR SETTINGS`
and give Sergey these UI steps rather than using a credential workaround:

1. Open repository **Settings -> Branches**. Inspect existing rules/rulesets
   first; edit the matching develop rule, or **Add classic branch protection
   rule** if none exists. Branch name pattern must be exactly `develop`.
2. Enable **Require a pull request before merging**. Leave **Require approvals**
   unchecked: solo development must not require a second human reviewer.
3. Enable **Require status checks to pass before merging**. Select the exact
   `validation / Backend quality gate` check from GitHub Actions. Enable
   **Require branches to be up to date before merging**. If the check is absent,
   wait for a real PR run to publish it; do not invent a replacement context.
4. Enable **Require conversation resolution before merging** and
   **Do not allow bypassing the above settings**. Do not add PR bypass actors.
5. Leave **Allow force pushes** and **Allow deletions** unchecked. Save, reopen
   the rule and verify all settings and its exact develop-only scope.
6. Confirm a real PR reports the intended check as required. Record the settings
   evidence in the issue/PR. Independent Codex review is separate PR evidence
   under [the review contract](development-process.md#independent-review-contract),
   not a fabricated GitHub status check or human-review count.

Rulesets may enforce equivalent controls when already in use; do not introduce
conflicting overlapping rules. No rule should be claimed active without reading
the saved GitHub settings.

## Validation and deployment boundaries

- Task branch pushes do not deploy staging.
- PRs to develop run `backend-pr-checks.yml` -> reusable `validate.yml` only.
  This uses `pull_request`, not `pull_request_target`, with read-only contents
  permissions and no staging environment or deployment secrets. Fork PRs must
  not gain privileged credentials through validation or artifact execution.
- A push to develop (normally the human merge) runs staging workflow validation
  -> deploy (`needs: validation`) -> HP-75 smoke (`needs: deploy`). Smoke uses
  the protected `staging-smoke` environment and restricted diagnostic identity.
  Both dependencies must succeed; do not add `always()` to bypass them.
- Backend/frontend smoke verify their own repository's SHA, never compare
  unrelated cross-repository SHAs. See [staging smoke](../quality-staging-smoke.md).
- Production workflows, master, VERSION and release authorization are unchanged.

## Issue structure

Each GitHub milestone has one tracking issue containing:

- link to its canonical DoD row;
- approved scope and non-goals;
- ordered child issue checklist;
- milestone-level build/test/manual evidence;
- risks and rollback confirmation.

The tracking issue is the canonical location for actual results, CI links,
manual checks and completion state. Do not store this evidence in
`docs/roadmap/definition-of-done.md`.

Each implementation issue should fit one small completed slice and contain:

- context and confirmed code evidence;
- milestone and debt IDs;
- explicit scope and non-goals;
- acceptance criteria;
- automated and manual verification;
- API/schema/config impact;
- rollback steps;
- ADR/DoD-criteria/debt update requirements.

Use the repository Issue Forms. Do not start an unassigned architecture issue
without first mapping it to the approved roadmap or the debt registry.
