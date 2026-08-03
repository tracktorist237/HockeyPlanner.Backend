# Backend Tech Debt Registry

Status: **Approved baseline**

The registry records confirmed debt. It does not change roadmap scope or
authorize a Constitution exception by itself.

Priority mapping is fixed: `P0` = critical, `P1` = high, `P2` = medium and
`P3` = low. Allowed statuses are `Open`, `Planned`, `In Progress`, `Blocked`,
`Accepted` and `Resolved`.

## Security debt

| ID | Description | Priority | Impact | Milestone / notes | Evidence | Status |
|---|---|---|---|---|---|---|
| SEC-001 | Trust in client-supplied `currentUserId` | P0 | User impersonation | M2-M3 | `HockeyPlanner.Backend.WebAPI/Controllers/TeamsController.cs` (`TeamsController` actor-query endpoints); `HockeyPlanner.Backend.WebAPI/Controllers/NotificationsController.cs` (`GetNotifications`, `MarkRead`, `MarkAllRead`, `GetPreferences`, `UpdatePreferences`, `SendTest`) | Open |
| SEC-002 | Public Users CRUD and entity mass assignment | P0 | Hash exposure and role escalation | M3 | `HockeyPlanner.Backend.WebAPI/Controllers/UsersController.cs` (`GetUsers`, `PostUser`) | Open |
| SEC-003 | Cross-team event update and unprotected event/roster reads | P0 | IDOR and private-data disclosure | M2 | `HockeyPlanner.Backend.Application/Implementations/Services/EventService.cs` (`UpdateEvent`, `GetEvent`); `HockeyPlanner.Backend.WebAPI/Controllers/LinesController.cs` (`GetRosterByEvent`) | Open |
| SEC-004 | Arbitrary player deletion by identifier | P0 | Roster corruption | M2 | `HockeyPlanner.Backend.WebAPI/Controllers/PlayersController.cs` (`RemovePlayerById`) | Open |
| SEC-005 | Refresh race, logout ownership and active reset siblings | P1 | Session/token compromise | M4 | `HockeyPlanner.Backend.WebAPI/Controllers/AuthController.cs` (`Refresh`, `Logout`, `ResetPassword`) | Open |
| SEC-006 | Raw authentication tokens in fallback/development logs | P1 | Account compromise through logs | M4-M5 | `HockeyPlanner.Backend.WebAPI/Services/LoggingAuthEmailSender.cs` (`LoggingAuthEmailSender`); `HockeyPlanner.Backend.WebAPI/Controllers/AuthController.cs` (`LogQueuedEmailTimeout`) | Open |

## Architectural debt

| ID | Description | Priority | Impact | Milestone / notes | Evidence | Status |
|---|---|---|---|---|---|---|
| ARC-001 | Application references Infrastructure | P1 | Use cases cannot be isolated | M12; Revisit in M12 | `HockeyPlanner.Backend.Application/HockeyPlanner.Backend.Application.csproj` (project references) | Accepted |
| ARC-002 | Controllers query `AppDbContext` directly | P1 | HTTP, authorization and persistence are mixed | M7-M8, M12 | `HockeyPlanner.Backend.WebAPI/Controllers/TeamsController.cs` (`TeamsController`); `HockeyPlanner.Backend.WebAPI/Controllers/AdminController.cs` (`AdminController`); `HockeyPlanner.Backend.WebAPI/Controllers/GoaliesController.cs` (`GoaliesController`) | Open |
| ARC-003 | EF entities are API contracts | P1 | Data exposure and fragile contracts | M3, M12 | `HockeyPlanner.Backend.WebAPI/Controllers/UsersController.cs` (`GetUsers`, `PostUser`) | Open |
| ARC-004 | Multi-feature controllers are very large | P2 | High regression risk | M7-M8 | `HockeyPlanner.Backend.WebAPI/Controllers/TeamsController.cs` (`TeamsController`); `HockeyPlanner.Backend.WebAPI/Controllers/AdminController.cs` (`AdminController`) | Open |
| ARC-005 | Error handling is duplicated and inconsistent | P2 | Incompatible client behavior | M5 | `HockeyPlanner.Backend.WebAPI/Controllers/LinesController.cs` (`LinesController`); `HockeyPlanner.Backend.WebAPI/Controllers/ScheduledEventController.cs` (`ScheduledEventController`) | Open |

## Technical debt

| ID | Description | Priority | Impact | Milestone / notes | Evidence | Status |
|---|---|---|---|---|---|---|
| TECH-001 | An owner can leave a team without an owner | P1 | Unmanageable team | M7 | `HockeyPlanner.Backend.WebAPI/Controllers/TeamsController.cs` (`LeaveTeam`, `JoinTeamInternal`) | Open |
| TECH-002 | Roster uniqueness is scoped to a line, not an event | P1 | Duplicate users/positions | M7 | `HockeyPlanner.Backend.Infrastructure/Data/Configurations/PlayerConfiguration.cs` (`PlayerConfiguration`); `HockeyPlanner.Backend.Application/Implementations/Services/LineService.cs` (`CreateRoster`, `UpdateRoster`) | Open |
| TECH-003 | Existing attendance update and roster removal are not atomic | P1 | Partially updated event | M7 | `HockeyPlanner.Backend.Application/Implementations/Services/EventService.cs` (`UpdateAttendance`) | Open |
| TECH-004 | Goalie status transitions have no explicit state machine | P1 | Invalid application states | M8 | `HockeyPlanner.Backend.WebAPI/Controllers/GoaliesController.cs` (`Apply`, `UpdateStatus`) | Open |
| TECH-005 | Main notification path does not persist delivery records | P1 | Incomplete delivery journal | M6 | `HockeyPlanner.Backend.WebAPI/Services/NotificationService.cs` (`NotifyUsersAsync`) | Open |
| TECH-006 | Save-then-notify can produce false failures and duplicates | P1 | Unsafe retries | M6 | `HockeyPlanner.Backend.Application/Implementations/Services/EventService.cs` (`CreateEvent`); `HockeyPlanner.Backend.WebAPI/Controllers/TeamsController.cs` (`CreateTeamNews`); `HockeyPlanner.Backend.Application/Implementations/Services/LineService.cs` (`UpdateRoster`); `HockeyPlanner.Backend.WebAPI/Controllers/AdminController.cs` (`PublishRelease`) | Open |
| TECH-007 | `DateTime` and `ToUniversalTime` semantics are ambiguous | P2 | Shifted event/birthday values | M11-M12 | `HockeyPlanner.Backend.Application/Implementations/Services/EventService.cs` (`EventService`); `HockeyPlanner.Backend.WebAPI/Controllers/UsersController.cs` (`UsersController`) | Open |

## Infrastructure debt

| ID | Description | Priority | Impact | Milestone / notes | Evidence | Status |
|---|---|---|---|---|---|---|
| INF-001 | Production migration failure is caught and ignored | P0 | API starts on incompatible schema | M10 | `HockeyPlanner.Backend.WebAPI/Program.cs` (startup migration block) | Open |
| INF-002 | Health response does not test PostgreSQL | P1 | False readiness | M10 | `HockeyPlanner.Backend.WebAPI/Program.cs` (`GetHealthResponse`) | Open |
| INF-003 | No versioned backup job and restore rehearsal in the repository | P1 | Data-loss recovery risk | M10 | `infra/docker-compose.yml` (configuration file); `HockeyPlanner.Backend.WebAPI/Controllers/AdminController.cs` (`DownloadDatabaseBackup`) | Open |
| INF-004 | Storage, nginx and application limits/configuration drift | P2 | Environment-specific failures | M9-M10 | `HockeyPlanner.Backend.WebAPI/appsettings.json`, `infra/docker-compose.yml`, `infra/nginx/hockeyplanner.conf` (configuration files) | Open |
| INF-005 | Request-scoped email uses fire-and-forget `Task.Run` | P1 | Lost email during shutdown | M6 | `HockeyPlanner.Backend.WebAPI/Controllers/AuthController.cs` (`QueueEmailConfirmation`, `QueuePasswordReset`) | Open |
| INF-006 | Replaced storage objects are not deleted | P2 | Unbounded object storage | M9 | `HockeyPlanner.Backend.WebAPI/Services/IFileStorageService.cs` (`DeleteAsync`); `HockeyPlanner.Backend.WebAPI/Services/S3FileStorageService.cs` (`DeleteAsync`); `HockeyPlanner.Backend.WebAPI/Services/ImageKitUploader.cs` (`DeleteAsync`); no caller found | Open |

## Performance debt

| ID | Description | Priority | Impact | Milestone / notes | Evidence | Status |
|---|---|---|---|---|---|---|
| PERF-001 | Event details load several collection graphs together | P2 | Cartesian query growth | M7 | `HockeyPlanner.Backend.Application/Implementations/Services/EventService.cs` (`GetEvent`) | Open |
| PERF-002 | Goalie DTO mapping performs queries per user/application | P2 | Slow goalie screen | M8 | `HockeyPlanner.Backend.WebAPI/Controllers/GoaliesController.cs` (`LoadAvailableGoalies`, `ToApplicationDto`) | Open |
| PERF-003 | Users/events/releases and birthday scans can be unbounded | P3 | Future memory and latency growth | Deferred; revisit in M10-M12 | `HockeyPlanner.Backend.WebAPI/Controllers/UsersController.cs` (`GetUsers`); `HockeyPlanner.Backend.WebAPI/Services/BirthdayPushHostedService.cs` (`ExecuteAsync`) | Accepted |
| PERF-004 | Broadcast push is sequential inside an HTTP request | P2 | Request timeout under load | M6 | `HockeyPlanner.Backend.WebAPI/Services/NotificationService.cs` (`NotifyUsersAsync`) | Open |
