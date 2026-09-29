# PostgreSQL notification guarantees (HP-74)

These tests use the isolated PostgreSQL Testcontainer through the common
WebApplicationFactory. Separate scopes create separate DbContexts/connections;
no EF InMemory provider is used. Push and email adapters are test doubles, so
no external notification providers receive requests.

## Coverage map

| Guarantee / failure window | Regression evidence |
| --- | --- |
| Concurrent logical enqueue, duplicate recipients, later repeated request | `NotificationOutboxTests.ConcurrentEnqueue_LogicalIdentityCreatesOneNotificationAndJobPerUser`: both connections rendezvous before enqueue; one notification/job per recipient |
| Database uniqueness independent of application advisory locks | `NotificationJobPersistenceTests.ConcurrentInserts_DatabaseUniquenessRejectsLoserWithoutOrphanWork`: two independent writers for logical identity and notification/job identity; exactly one commit, one PostgreSQL unique violation on the expected constraint |
| Business rollback cannot leave notification/job work | `NotificationOutboxTests.OuterTransactionRollback_RemovesBusinessChangeNotificationAndJob`, `NotificationJobPersistenceTests.BusinessChangeAndJob_RollBackTogether` |
| Two processors cannot deliver the same job, even when the claim looks stale | `NotificationJobProcessorTests.ConcurrentScopes_CannotSendSameJobEvenAfterClaimTimeout`: first fake provider is held at a gate; second processor cannot replace the persisted claim or increment attempts; final completion verified |
| Crash before delivery and after external send but before durable acknowledgement | `NotificationJobProcessorTests.DatabaseFailureDuringDelivery_LeavesClaimRecoverable`: targeted command-interceptor database interruption, persisted processing claim, later new-scope recovery; attempt and completion state asserted |
| Stale claim recovery / bounded retries / terminal failure | `StaleClaim_IsRecoveredInAnotherScope`, `TransientFailure_Backoff_ThenSuccess`, `Failures_AreBounded`, `ExhaustedStaleClaim_IsTerminalWithoutSending` |
| Partial endpoint success is not resent | `PartialDelivery_RetryDoesNotRepeatSuccessfulEndpoint`: exact endpoint sequence, not only aggregate send count, plus durable sent records |
| Completed endpoint acknowledgement survives incomplete job completion | `CrashAfterEndpointAcknowledgement_DoesNotResendOnRecovery` |
| Cancellation releases locks and keeps durable work recoverable | `Cancellation_LeavesRecoverableClaimAndReleasesLock`; hosted-loop shutdown is covered by `NotificationBackgroundWorkerTests` |
| Empty database / existing installation upgrades | Mandatory `MigrationReadinessTests`; see [historical baseline and production adoption](quality-migration-baseline.md) |

The database interruption tests inject a driver failure at a specific SQL
boundary while keeping real PostgreSQL storage/transactions. They are not a
claim to simulate every network partition or operating-system crash.

## Determinism and delivery limits

Concurrency gates use TaskCompletionSource and bounded waits, not sleeps.
Retry/recovery uses an injected clock; tests do not wait for real backoff.
The provider callback asserts that no database transaction spans external IO.

Logical notification/job uniqueness is database-enforced. A persisted successful
endpoint is skipped on retry. An external send that succeeds just before loss
of its database acknowledgement can repeat: external delivery is at least once,
not exactly once. The regression explicitly expects that possible duplicate
instead of marking the interrupted job terminal and silently losing work.

## Run locally / in CI

Docker must be available. A missing Docker engine is a failure, not a skipped
suite or permission to substitute EnsureCreated for migration coverage.

```powershell
dotnet test --filter 'FullyQualifiedName~Notification|FullyQualifiedName~LeagueNotificationBatch|FullyQualifiedName~AuthEmailOutbox|FullyQualifiedName~MigrationReadiness'
dotnet test
```

CI also runs MigrationReadinessTests explicitly before the full suite. No test
in this coverage accesses staging or production databases.
