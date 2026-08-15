# ADR-003: Authentication token lifecycle

- Status: Accepted
- Date: 2026-08-16
- Milestone: M4
- Owners: HockeyPlanner maintainers
- Related issues/debt: HP-23 through HP-29, SEC-005, SEC-006

## Context

Authentication currently spans JWT access tokens, opaque refresh tokens, email
confirmation tokens, password-reset tokens and frontend-cached user state. The
system needs one lifecycle contract before concurrency and ownership defects are
fixed, without coupling M4 to a broad controller or storage redesign.

## Decision

The canonical authenticated identity is `User.Id`, represented as a `Guid` in a
validated JWT. Server-side authorization treats the JWT identity exposed through
`ICurrentUser` as authoritative. Query, route, body and frontend-cached user IDs
are resource identifiers or compatibility data, never an alternative actor.

Refresh tokens are opaque credentials. Only their hashes are persisted. Rotation
must atomically consume the presented token and create its replacement so that an
old refresh token can be consumed exactly once, including under concurrent
requests. The persisted replacement relationship must identify the token created
by the successful rotation.

Logout remains token-specific. An authenticated actor may revoke only a refresh
token owned by that actor. Password reset and password change may revoke all
sessions for the affected account as separate account-security operations.

Issuing a password-reset token invalidates all older active reset tokens for the
account. A successful reset invalidates every reset token for the account and
revokes every refresh session. Repeated or concurrent consumption of one reset
credential may succeed at most once.

`LinkPlayer` must not permit arbitrary claiming of credential-less profiles.
Until a separate ownership-proof design is approved, that claiming operation is
disabled; M4 does not introduce claim codes, invitations or proof infrastructure.

Frontend-cached user data is not authoritative identity. Browser tabs share one
account session and must coordinate refresh so a stale tab cannot erase a newer
valid rotated session. Definitive session failure and logout clear credentials
and authenticated user state, including when the logout request itself fails.

Raw access, refresh, confirmation and reset tokens, and URLs containing such
tokens, must not be written to normal application logs. Tests obtain email tokens
through capture/test senders rather than production-style token logging.

## Alternatives considered

- **HttpOnly refresh-token cookies now.** Rejected for M4 because it changes the
  transport and deployment contract. It may be proposed separately.
- **Allow profile claiming with a new claim code.** Rejected because there is no
  approved ownership-proof product or domain model.
- **Treat cached frontend user state as a session.** Rejected because it cannot
  prove server-side identity or token validity.
- **Extract all authentication orchestration from `AuthController`.** Deferred to
  M12; M4 should make the existing lifecycle correct with narrow services or
  transactions where needed.

## Consequences

- Refresh and reset implementations require atomic persistence semantics and
  concurrency tests against PostgreSQL.
- Owner-bound logout requires a valid JWT in addition to the opaque refresh
  credential.
- Password reset intentionally signs out all sessions for the account.
- Existing unsafe `LinkPlayer` claiming becomes unavailable until superseded by
  an approved ownership-proof flow.
- Frontend session coordination must account for multiple tabs sharing storage.
- Teams and Admin identity cleanup remain M7, M8 and M12 work; this ADR does not
  expand M4 into those areas.

## Verification

- PostgreSQL integration tests cover refresh rotation and races, logout ownership,
  reset invalidation and races, and non-mutating disabled profile claiming.
- JWT integration tests prove malformed or ambiguous authenticated identity fails
  closed.
- Captured-log tests prove raw auth tokens and token-bearing URLs are absent.
- Frontend tests cover definitive failure, logout cleanup, cached-user handling and
  cross-tab refresh ordering.

## Rollback or supersession

An implementation slice may be rolled back independently while retaining these
expectations as skipped security tests. Changing session transport, profile
ownership proof or account-wide revocation semantics requires a superseding ADR.
