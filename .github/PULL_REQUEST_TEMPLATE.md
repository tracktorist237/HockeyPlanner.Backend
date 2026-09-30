## Milestone and issue

- Milestone:
- Linked issue (keep In Progress until post-merge verification):
- Related debt IDs:
- Related ADRs:

## Outcome

Describe the completed user/developer outcome and why the change is needed.

## Scope

- Included:
- Explicitly not included:
- Base SHA:
- Head SHA:
- Changed files / diff summary:

## Architecture review

- [ ] Backend Constitution and Architecture Principles were followed.
- [ ] No forbidden project dependency was added.
- [ ] Controllers remain transport-only.
- [ ] Identity/resource authorization is correct.
- [ ] DTO, transaction, cancellation, time, file and logging rules were checked.
- [ ] Full Code Review Checklist was completed (or N/A reasons are below).

## Compatibility impact

- API: none / describe approved change
- Database: none / migration and compatibility plan
- Configuration/infrastructure: none / describe
- External services: none / describe

## Verification

```text
dotnet restore:
dotnet build:
dotnet test:
git diff --check:
```

Manual checks performed:

1.

Known warnings / checks not run (with reason):

## Independent review (required before human merge)

- Reviewer: separate Codex session/agent, not the implementation session
- Session/report link:
- Reviewed head SHA:
- Review state: pending / completed
- Verdict when completed: `APPROVE`, `CHANGES REQUIRED`, or `BLOCKED / NEEDS HUMAN DECISION`
- Findings and author resolutions:
- [ ] Approval and green mandatory CI cover the current head; conversations resolved.
- [ ] Develop protection verified; human merge only. No master/release changes.

Self-review is not independent review. New commits require re-review. `pending`
is for draft handoff only. Do not mark Done until develop validation, staging
deploy and HP-75 smoke succeed. Any narrow process exception must be explicitly
named with authorization; it never bypasses failed CI or protection.

## Documentation

- [ ] Milestone tracking issue updated with CI, manual-check and completion
      evidence if applicable.
- [ ] Tech Debt Registry updated if debt was found/resolved/accepted.
- [ ] ADR created/updated if required.
- [ ] No documentation update required (explain below).

## Risk and rollback

- Primary regression risk:
- Monitoring/verification after deploy:
- Smallest safe rollback:
