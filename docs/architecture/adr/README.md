# Architecture Decision Records

ADRs record decisions; they do not replace the Constitution or roadmap. The
Backend Constitution is the primary rule by default. An accepted ADR may
authorize only a specific, explicitly described and limited exception; it
cannot silently override the Constitution as a whole. The exception must state
its scope, reason and consequences.

## Status values

- `Proposed`
- `Accepted`
- `Superseded by ADR-NNN`
- `Rejected`

## Planned registry

| ADR | Purpose | Write before | Decision scope |
|---|---|---|---|
| ADR-001 Target project dependencies | Prevent dependency cycles | M12; observe from M1 | Project responsibilities and references |
| ADR-002 Current user and resource authorization | Close systemic IDOR | M2 | `ICurrentUser`, roles and resource policies |
| [ADR-003 Authentication token lifecycle](ADR-003-authentication-token-lifecycle.md) | Define refresh/reset/logout behavior | M4 | Rotation, reuse, revoke, expiry and sessions |
| ADR-004 API error contract | Prevent competing error shapes | M5 | ProblemDetails, status mapping and correlation |
| ADR-005 Background work and delivery semantics | Separate storage from transport | M6 | Best-effort, retry, idempotency and future outbox boundary |
| ADR-006 Transaction ownership | Prevent partial state | M7 | Transaction start/end and commit ownership |
| ADR-007 Team ownership and role transitions | Protect owner invariant | M7 | Transfer, leave, removal and admin permissions |
| ADR-008 Goalie state machine | Define legal transitions | M8 | Actors, status graph, capacity and concurrency |
| ADR-009 File storage lifecycle | Align S3 and ImageKit behavior | M9 | Object key, URL, visibility, replace and delete |
| ADR-010 Production migrations and rollback | Make deploy recoverable | M10 | Startup migration, backup and rollback policy |
| ADR-011 Time representation | Remove timezone ambiguity | M11-M12 | Instants, offsets and calendar dates |
| ADR-012 DTO and API compatibility | Protect frontend contracts | M12 | DTO placement, enum and nullable semantics |
| ADR-013 Logging and sensitive data | Prevent secret/PII leakage | M5-M10 | Redaction, allowed context and retention |

Create an ADR from [template.md](template.md) only when the rules in the
[Development Process](../../governance/development-process.md) apply. Accepted
ADRs are immutable; corrections use a superseding ADR.
