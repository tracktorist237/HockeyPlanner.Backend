## Milestone and issue

- Milestone: MNN
- Closes:
- Related debt IDs:
- Related ADRs:

## Outcome

Describe the completed user/developer outcome and why the change is needed.

## Scope

- Included:
- Explicitly not included:

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
