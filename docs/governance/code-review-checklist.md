# Backend Pull Request Review Checklist

Status: **Mandatory for every pull request**

Items may be marked `N/A` only with a short reason in the PR.

## Scope and contracts

- [ ] The PR belongs to one approved milestone and linked issue.
- [ ] The change is small, coherent and independently reversible.
- [ ] Non-goals are explicit; no adjacent refactoring is included.
- [ ] Public routes, payloads, status codes, enum and nullable semantics remain
      compatible or have explicit approval and contract tests.
- [ ] No generated file, migration or package change is unrelated to the issue.

## Controllers and API

- [ ] Controllers do not use `AppDbContext` or persistence entities.
- [ ] Controllers contain no business rules, role queries or transactions.
- [ ] Identity comes from `ICurrentUser`, not route/query/body data.
- [ ] Resource authorization is performed for every resource identifier.
- [ ] Request and response types are DTOs; EF entities do not cross HTTP.
- [ ] Input strings, collections, enums, pagination and uploads are bounded.
- [ ] Status codes and error payloads follow the approved error contract.
- [ ] Growing collections use pagination with a maximum page size.

## Domain and application

- [ ] The use case has one clear responsibility and transaction owner.
- [ ] Business invariants are centralized rather than copied into controllers.
- [ ] Status changes validate both actor and source-to-target transition.
- [ ] `AppRole` and `TeamRole` remain separate.
- [ ] Source and target resources are authorized for cross-team operations.
- [ ] Repeated/idempotent requests have explicit behavior.
- [ ] External-delivery failure cannot falsify a committed business result.
- [ ] No new abstraction exists without a current boundary or duplication need.

## EF Core and data integrity

- [ ] Read-only queries use `AsNoTracking`.
- [ ] `Where`, `Select`, `Skip` and `Take` precede materialization.
- [ ] There is no client-side filtering of a large table after `ToList`.
- [ ] DTO projection is preferred to unnecessary `Include` graphs.
- [ ] Multiple collection loads avoid Cartesian explosion.
- [ ] No query is executed per item in a loop.
- [ ] Already loaded data is not queried repeatedly in one request.
- [ ] `ExecuteUpdate/Delete` participates in the required transaction.
- [ ] Nested services do not hide extra commits.
- [ ] External IO is outside database transactions.
- [ ] Unique/check/FK/cascade behavior matches the application invariant.
- [ ] Concurrency, duplicate requests and lost updates were considered.
- [ ] Migration data cleanup and rollback are documented when applicable.

## Security and privacy

- [ ] Authentication requirement is explicit.
- [ ] IDOR was checked for each route, query and body identifier.
- [ ] DTO binding cannot assign roles, hashes, confirmation flags or ownership.
- [ ] Team members cannot access another team's private resources.
- [ ] Refresh/reset/confirmation tokens validate expiry, use and ownership.
- [ ] Admin endpoints use the SuperAdmin policy.
- [ ] Anonymous endpoints consider brute force and account enumeration.
- [ ] Logs and errors contain no secrets, raw tokens, endpoints or excess PII.
- [ ] File validation uses content as well as extension/MIME.
- [ ] File visibility matches user privacy expectations.

## Async and external services

- [ ] `CancellationToken` flows through controller, service, EF and HTTP calls.
- [ ] There is no `.Result`, `.Wait()`, synchronous network/file IO or request
      `Task.Run`.
- [ ] HttpClient comes from the configured factory and has a timeout.
- [ ] Retry is bounded and safe for the operation's idempotency.
- [ ] Background work supports repeated execution and graceful shutdown.
- [ ] File replacement and deletion have a defined lifecycle.

## Operations and configuration

- [ ] New configuration is validated at startup and documented for environments.
- [ ] Development, staging, production, Docker and nginx remain aligned.
- [ ] Health/readiness accurately represents required dependencies.
- [ ] Logs are structured and avoid string interpolation.
- [ ] Deploy and migration behavior remains repeatable and reversible.
- [ ] Backup/restore implications were considered for schema/data changes.

## Architecture and verification

- [ ] No forbidden project reference was added.
- [ ] Core remains independent of EF, ASP.NET Core and external SDKs.
- [ ] Application does not gain a new Infrastructure dependency.
- [ ] DI registrations and lifetimes match service responsibilities.
- [ ] Unit tests cover changed business rules.
- [ ] PostgreSQL integration tests cover constraints/transactions where relevant.
- [ ] Contract tests protect changed HTTP behavior.
- [ ] Negative authorization tests cover the affected resources.
- [ ] Build, full tests, manual smoke and `git diff --check` passed.
- [ ] ADR, DoD and Tech Debt Registry impact is recorded.

