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

## Branch protection

For `master` and `develop` configure GitHub branch protection to:

- require a pull request before merge;
- require the `Backend PR Checks / Build and test` status check;
- require resolved review conversations;
- block force pushes and branch deletion;
- apply the rules to administrators as well when emergency access is not
  required.

Repository settings, not this file alone, make the check mandatory. The check
is defined in `.github/workflows/backend-pr-checks.yml`.

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
