# Milestone Definition of Done

Status: **Approved and mandatory**

| Milestone | Goal and entry conditions | Required outcome | Verification | Rollback criterion |
|---|---|---|---|---|
| M1 | Freeze behavior before refactoring; backend builds | Test foundation, core characterization, contract snapshots and authorization matrix exist | `dotnet build`, `dotnet test`, current-flow smoke test | Test-only changes can be removed without production impact |
| M2 | Remove trust in `currentUserId`; M1 green | JWT identity and resource checks protect event, line, player and attendance operations | Two-user/two-team security tests and GUID-substitution smoke test | Revert identity integration without DB changes |
| M3 | Secure users, push and notifications; M2 complete | No entity mass assignment; subscription and notification resources belong to JWT user | Profile/privacy/push/preferences/test-notification tests | Revert controller/application slice while retaining M2 |
| M4 | Stabilize account and token lifecycle; M2-M3 complete | Atomic refresh rotation, owner-bound logout, reset invalidation and safe link-player flow | Register/login/refresh/logout/reset/link-player and concurrency tests | Revert auth slice; schema change, if any, rolls back separately |
| M5 | Establish one error contract; snapshots available | Global error handling replaces repeated controller catches and preserves statuses | Contract tests for 400/401/403/404/409/500 | Revert middleware and handler extraction together |
| M6 | Make background delivery reliable; M4-M5 complete | No request `Task.Run`; jobs shut down cleanly; push/email failure does not falsify committed business result | Fake-adapter, cancellation, duplicate-run and shutdown tests | Restore previous sender/job adapter without API changes |
| M7 | Separate team use cases; M2 and M5 complete | Team-role rules are centralized, owner invariant and API snapshots hold | Create/join/leave/role/news/media/event access suite | Revert the controller/service slice |
| M8 | Separate admin and goalie use cases; M2, M5-M6 complete | SuperAdmin policy and goalie transition rules are centralized; critical N+1 removed | Admin access matrix, transition and concurrent-apply tests | Revert feature services without contract changes |
| M9 | Isolate external integrations and files; M5-M6 complete | Configured clients, timeouts/retry, validation and provider selection are explicit | Adapter tests plus staging S3/ImageKit/email/push smoke | Switch DI back to previous adapter |
| M10 | Make production deploy recoverable; stable build | Real readiness, fail-fast migrations, verified backup/restore and aligned environment config | Docker smoke, clean migration, restore rehearsal, staging deploy | Previous image plus compatible DB; no irreversible deploy |
| M11 | Complete async and dependency hygiene; M7-M9 complete | Request cancellation flows end-to-end; DI lifetimes and sync IO are clean | Build, full tests and cancellation integration tests | Revert signature/registration changes as one unit |
| M12 | Enforce final boundaries; M1-M11 complete | Application no longer references Infrastructure; controllers have no DbContext; API uses DTOs | Architecture tests, reference audit and contract snapshots | Revert class moves while retaining contracts |

## Update rule

This document is static and contains criteria only. Actual results, CI links,
manual checks and completion state are stored in the corresponding GitHub
milestone tracking issue. Completing an individual task does not require an
edit to this file. Changing a criterion requires explicit approval and a
separate documentation change; an ADR alone cannot silently rewrite the
roadmap.
