# Durable notification delivery (M6)

## Transaction boundary

`NotificationService` now enqueues only. `NotificationOutbox` saves in-app
notifications, push jobs and pending business changes in the same PostgreSQL
transaction. It joins an existing transaction when present. Push/email network
calls occur only in `NotificationJobProcessor`, with no open DB transaction.

Call-site audit:

| Producer | Durable boundary |
| --- | --- |
| EventService.CreateEvent | Event, attendance, in-app notifications and jobs |
| EventDataTransferService | Final attendance/roster/deletion and affected-user notifications |
| LineService.UpdateRoster | Roster replacement and roster-ready notifications |
| TeamsController.CreateTeamNews | News and requested team notifications |
| GoaliesController.Apply/Propose/UpdateStatus | Application/status and goalie/manager notifications |
| AdminController.PublishRelease | Release publication and deduplicated release notifications |
| BirthdayPushHostedService | Date/recipient logical identity; restart does not duplicate notification |
| PushNotificationsController broadcast | One durable notification/job per recipient |
| UserNotificationService/AdminController self-test | Durable notification/job, JWT actor preserved |
| External league manual/background sync | Each event transaction appends to a durable team-operation batch |
| Auth register/change-email/resend/forgot-password | User/token state and encrypted email job |

Ordinary attendance/conflict checks do not produce notifications today; M6 adds
none. Legacy SPbHL facade retains its existing notification semantics. No direct
push or fire-and-forget auth email remains in business request paths.

League batches hold a dedicated session advisory lock during the logical team
operation. Finalization creates one aggregated created-events notification per
recipient and preserves background-only reschedule notifications. The worker
recovers unfinished batches after the lock is released on failure/restart.
Each batch finalization is transactional and idempotent. A finalized batch cannot
accept late event commits after losing its operation lock.

## Delivery guarantees

Unique `(user_id, logical_key)` and unique job identities prevent duplicate
in-app notifications for the same logical operation. A PostgreSQL session lock
covers each delivery job; stale persisted claims recover after timeout. Delivery
success is saved per subscription, so partial retries skip successful endpoints.

External push/email delivery is **at least once**, not exactly once: provider
acceptance followed by process failure before the DB acknowledgement can repeat
a send. SMTP and Web Push do not offer a transactional acknowledgement with our
database. Separate business operations remain separate logical notifications.

Auth job payloads contain only an AES-GCM encrypted token plus DB identifiers,
never email addresses or message bodies. The encryption key is derived from the
existing JWT signing secret with a separate context. Drain pending email jobs
before rotating that secret. Used/expired tokens are skipped; terminal jobs erase
the encrypted payload. Do not log payloads, push endpoints, tokens or provider
response bodies.

## Schema rollout

Apply the additive EF migrations through the normal release procedure before
running the new worker. Existing notification rows have a null logical key and
are not re-sent. Existing worker jobs retain push kind (0). No historical
notifications are backfilled into the queue.

For an application rollback, retain the additive schema and stop the new worker.
Do not downgrade the schema with durable work present. The auth migration Down
explicitly refuses a downgrade while email jobs exist. Completed/failed records
are retained for diagnosis; retention policy is a separate operational decision.
