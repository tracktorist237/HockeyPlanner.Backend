# Hockey Planner Backend Constitution

Status: **Approved and mandatory**

## Philosophy

Hockey Planner Backend remains a modular monolith. Refactoring must make
business rules, security boundaries and transactions explicit without adding
enterprise complexity or changing the public API unnecessarily.

- Preserve behavior and API contracts before improving internal structure.
- Security and business invariants take priority over layer purity.
- Deliver small, complete and independently reversible changes.
- External delivery must not determine whether a committed business operation
  is reported as successful.
- Existing deviations belong in the Tech Debt Registry; do not create new ones
  silently.
- Add abstractions only at real boundaries: persistence, current user, time,
  files, email, push and external HTTP services.

## Projects and dependencies

| Project | Responsibility | Must not contain |
|---|---|---|
| `HockeyPlanner.Backend.Core` | Entities, enums, domain states, pure rules and exceptions | ASP.NET Core, EF Core, HTTP, configuration or external SDKs |
| `HockeyPlanner.Backend.Application` | Use cases, orchestration, resource authorization and infrastructure abstractions | Infrastructure references, HTTP context or direct external calls |
| `HockeyPlanner.Backend.Infrastructure` | EF Core, PostgreSQL, migrations and persistence implementations | HTTP endpoints, API DTOs or business decisions |
| `HockeyPlanner.Backend.WebAPI` | HTTP transport, JWT, middleware, DI composition and external adapters | Domain rules and persistence orchestration in controllers |
| `HockeyPlanner.Backend.Shared` | Transitional shared contracts | EF entities and infrastructure-specific types |

Target direction: `WebAPI -> Application -> Core` and
`Infrastructure -> Application abstractions + Core`. `Core` depends on nothing.
Existing deviations are removed only by the approved roadmap.

## Controllers

Controllers bind and validate HTTP input, obtain identity through
`ICurrentUser`, call one application use case, return DTOs and pass the request
`CancellationToken`.

Controllers must not use `AppDbContext`, decide roles or status transitions,
open transactions, call storage/email/push directly, use `Task.Run`, return EF
entities or duplicate exception handling.

## Application services

An application service represents a complete use case. It loads the actual
resource, performs resource authorization, validates invariants, owns the
transaction boundary, persists changes and returns an application result.

It must not trust `UserId`, `TeamId`, role or ownership supplied by the client.

## EF Core

- Use `AsNoTracking` for read-only queries.
- Apply `Where`, projection and pagination before materialization.
- Prefer projection to DTO over large `Include` graphs.
- Use projection or split queries when loading multiple collections.
- Keep `SaveChangesAsync` under one transaction owner.
- Treat `ExecuteUpdate` and `ExecuteDelete` as immediate writes and include them
  in explicit transactions when combined with other changes.
- Protect important invariants in code and, where possible, in PostgreSQL.
- Review existing data before changing constraints or cascade behavior.
- A production migration failure must stop application startup.
- Do not use EF InMemory to verify PostgreSQL constraints or transactions.

## Authorization

- JWT `sub`, exposed through `ICurrentUser`, is the identity source.
- Client-supplied `currentUserId` may remain temporarily for compatibility but
  must be ignored by the server.
- `AppRole` and `TeamRole` are separate concepts.
- Resource authorization happens after loading the stored resource.
- Operations moving data between teams require access to source and target.
- Every route, query and body identifier must be checked against an authorized
  resource. Knowing a GUID is not authorization.
- SuperAdmin access uses a dedicated policy.

## Transactions and external delivery

- One business operation has one atomic transaction boundary.
- Attendance and dependent roster changes are saved together.
- Roster replacement and refresh-token rotation are all-or-nothing operations.
- External HTTP, email, push and file calls do not run inside DB transactions.
- Until a durable outbox is explicitly approved, external delivery is
  best-effort and must not turn a committed operation into a false failure.
- Idempotent commands must remain safe when repeated.

## DTO and API contracts

- EF entities are neither request nor response contracts.
- Request and response models are separate.
- Contracts never expose password/token hashes, credentials or navigation
  properties.
- Nullable/default semantics are fixed by contract tests.
- Existing numeric enum serialization does not change without an API decision.
- Validate incoming enum values, strings, collections and upload sizes.
- Growing public collections require pagination.
- Errors use the project's single ProblemDetails-compatible contract.

## Cancellation and time

- Pass `CancellationToken` from controller through EF and external calls.
- Do not use `CancellationToken.None` in a request path.
- Use `TimeProvider` for current time in testable business logic.
- Store instants in UTC and expose offset-aware timestamps.
- Model calendar dates separately from instants.
- Do not call `ToUniversalTime()` on an unspecified input timestamp.

## Files

- Validate size, MIME type and file signature.
- Keep the object key needed for lifecycle management.
- Save the new object and state successfully before deleting the replaced file.
- File access rules must match the privacy promised by the API.

## Logging and errors

- Use structured templates and stable operation/resource identifiers.
- Never log raw tokens, passwords, API/VAPID keys, full push endpoints or
  unnecessary personal data.
- Log external failures with safe context and return sanitized messages.
- Map known domain/application exceptions centrally.
- Unexpected exceptions are logged once with a correlation identifier.

## Adding a feature

1. Define invariants, actors and authorization boundaries.
2. Define request and response contracts.
3. Add the application use case and transaction boundary.
4. Implement persistence and external adapters behind existing boundaries.
5. Add a thin endpoint.
6. Add unit, PostgreSQL integration, contract and negative authorization tests
   appropriate to the risk.
7. Run build, tests, self-review and manual smoke checks.
8. Update ADR, DoD or Tech Debt Registry only when their update rules apply.
