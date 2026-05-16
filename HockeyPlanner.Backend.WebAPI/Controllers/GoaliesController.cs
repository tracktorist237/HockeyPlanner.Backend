using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Goalies;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/events/{eventId:guid}/goalies")]
    public class GoaliesController : ControllerBase
    {
        private static readonly TimeSpan ConflictWindow = TimeSpan.FromHours(3);

        private readonly AppDbContext _context;
        private readonly IWebPushService _webPushService;
        private readonly ILogger<GoaliesController> _logger;

        public GoaliesController(
            AppDbContext context,
            IWebPushService webPushService,
            ILogger<GoaliesController> logger)
        {
            _context = context;
            _webPushService = webPushService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<ActionResult<EventGoaliesDto>> GetEventGoalies(Guid eventId, [FromQuery] Guid currentUserId)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var scheduledEvent = await _context.Events
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == eventId);
            if (scheduledEvent == null)
            {
                return NotFound(new { message = "Мероприятие не найдено." });
            }

            var currentUser = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == currentUserId);
            if (currentUser == null)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var membership = scheduledEvent.TeamId.HasValue
                ? await _context.TeamMemberships
                    .AsNoTracking()
                    .FirstOrDefaultAsync(value => value.TeamId == scheduledEvent.TeamId.Value && value.UserId == currentUserId)
                : null;
            var canManage = membership?.Role == TeamMemberRole.Owner || membership?.Role == TeamMemberRole.Admin;
            var isGoalie = currentUser.PrimaryPosition == Position.Goalie;
            var request = await LoadRequest(eventId);
            var requestVisible = request != null && IsRequestVisible(request, isGoalie, membership != null, canManage);
            var myApplication = request?.Applications.FirstOrDefault(value => value.GoalieUserId == currentUserId);
            var currentUserConflict = isGoalie
                ? await FindConflict(currentUserId, scheduledEvent.StartTime, eventId)
                : null;

            var availableGoalies = canManage
                ? await LoadAvailableGoalies(scheduledEvent, request)
                : new List<GoalieUserDto>();

            var previousRequests = canManage
                ? await LoadPreviousRequests(scheduledEvent, eventId)
                : new List<GoalieRequestDto>();

            return Ok(new EventGoaliesDto
            {
                IsGoalie = isGoalie,
                IsTeamMember = membership != null,
                CanManage = canManage,
                CanApply = requestVisible &&
                    request!.Status == GoalieRequestStatus.Open &&
                    isGoalie &&
                    (myApplication == null || IsInactiveApplication(myApplication)),
                CurrentUserConflict = currentUserConflict,
                MyApplication = myApplication == null ? null : await ToApplicationDto(myApplication, scheduledEvent.StartTime, eventId),
                Request = requestVisible ? await ToRequestDto(request!, scheduledEvent.StartTime, eventId) : null,
                AvailableGoalies = availableGoalies,
                PreviousRequests = previousRequests
            });
        }

        [HttpPost("request")]
        public async Task<ActionResult<GoalieRequestDto>> UpsertGoalieRequest(
            Guid eventId,
            [FromQuery] Guid currentUserId,
            [FromBody] UpsertGoalieRequestRequest request)
        {
            var scheduledEvent = await _context.Events.FirstOrDefaultAsync(value => value.Id == eventId);
            if (scheduledEvent == null)
            {
                return NotFound(new { message = "Мероприятие не найдено." });
            }

            if (!scheduledEvent.TeamId.HasValue)
            {
                return BadRequest(new { message = "Для объявления нужен event с командой." });
            }

            if (!await CanManageTeam(scheduledEvent.TeamId.Value, currentUserId))
            {
                return Forbid();
            }

            var normalizedNeededCount = Math.Clamp(request.NeededCount, 1, 4);
            var goalieRequest = await _context.GoalieRequests
                .Include(value => value.Applications)
                .FirstOrDefaultAsync(value => value.EventId == eventId);

            if (goalieRequest == null)
            {
                goalieRequest = new GoalieRequest
                {
                    EventId = eventId,
                    TeamId = scheduledEvent.TeamId,
                    CreatedByUserId = currentUserId,
                    CreatedAt = DateTime.UtcNow
                };
                await _context.GoalieRequests.AddAsync(goalieRequest);
            }

            goalieRequest.NeededCount = normalizedNeededCount;
            goalieRequest.Visibility = request.Visibility;
            goalieRequest.ResponseMode = request.ResponseMode;
            goalieRequest.Status = GoalieRequestStatus.Open;
            goalieRequest.PriceText = NormalizeText(request.PriceText, 120);
            goalieRequest.Description = NormalizeText(request.Description, 2000);
            goalieRequest.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            goalieRequest = await LoadRequest(eventId) ?? goalieRequest;

            return Ok(await ToRequestDto(goalieRequest, scheduledEvent.StartTime, eventId));
        }

        [HttpPost("apply")]
        public async Task<ActionResult<GoalieApplicationDto>> Apply(
            Guid eventId,
            [FromQuery] Guid currentUserId,
            [FromBody] CreateGoalieApplicationRequest request,
            CancellationToken cancellationToken)
        {
            var scheduledEvent = await _context.Events.AsNoTracking().FirstOrDefaultAsync(value => value.Id == eventId, cancellationToken);
            var goalieRequest = await LoadRequest(eventId);
            if (scheduledEvent == null || goalieRequest == null)
            {
                return NotFound(new { message = "Объявление не найдено." });
            }

            if (goalieRequest.Status != GoalieRequestStatus.Open)
            {
                return BadRequest(new { message = "Набор вратарей закрыт." });
            }

            var currentUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(value => value.Id == currentUserId, cancellationToken);
            if (currentUser == null)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            if (currentUser.PrimaryPosition != Position.Goalie)
            {
                return BadRequest(new { message = "Откликнуться может только пользователь с позицией вратарь." });
            }

            var membership = scheduledEvent.TeamId.HasValue
                ? await _context.TeamMemberships.AsNoTracking().FirstOrDefaultAsync(value =>
                    value.TeamId == scheduledEvent.TeamId.Value && value.UserId == currentUserId, cancellationToken)
                : null;
            if (!IsRequestVisible(goalieRequest, true, membership != null, false))
            {
                return Forbid();
            }

            var shouldAutoAccept =
                goalieRequest.ResponseMode == GoalieRequestResponseMode.AutoAccept &&
                goalieRequest.Applications.Count(value =>
                    value.Status == GoalieApplicationStatus.Accepted ||
                    value.Status == GoalieApplicationStatus.Confirmed) < goalieRequest.NeededCount;

            var existing = goalieRequest.Applications.FirstOrDefault(value => value.GoalieUserId == currentUserId);
            if (existing != null)
            {
                if (!IsInactiveApplication(existing))
                {
                    return Ok(await ToApplicationDto(existing, scheduledEvent.StartTime, eventId));
                }

                existing.Status = shouldAutoAccept ? GoalieApplicationStatus.Accepted : GoalieApplicationStatus.Pending;
                existing.Source = GoalieApplicationSource.Application;
                existing.Message = NormalizeText(request.Message, 1000);
                existing.UpdatedAt = DateTime.UtcNow;
                UpdateRequestStatus(goalieRequest);
                await _context.SaveChangesAsync(cancellationToken);

                if (shouldAutoAccept)
                {
                    await SendGoaliePush(
                        currentUserId,
                        "Заявка принята",
                        $"Вас готовы взять на событие: {scheduledEvent.Title}",
                        $"/events/{eventId}",
                        cancellationToken);
                }

                return Ok(await ToApplicationDto(existing, scheduledEvent.StartTime, eventId));
            }

            var application = new GoalieApplication
            {
                GoalieRequestId = goalieRequest.Id,
                GoalieUserId = currentUserId,
                Status = shouldAutoAccept ? GoalieApplicationStatus.Accepted : GoalieApplicationStatus.Pending,
                Source = GoalieApplicationSource.Application,
                Message = NormalizeText(request.Message, 1000),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.GoalieApplications.AddAsync(application, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            if (shouldAutoAccept)
            {
                await SendGoaliePush(
                    currentUserId,
                    "Заявка принята",
                    $"Вас готовы взять на событие: {scheduledEvent.Title}",
                    $"/events/{eventId}",
                    cancellationToken);
            }

            application = await _context.GoalieApplications
                .Include(value => value.GoalieUser)
                .FirstAsync(value => value.Id == application.Id, cancellationToken);
            return Ok(await ToApplicationDto(application, scheduledEvent.StartTime, eventId));
        }

        [HttpPost("propose")]
        public async Task<ActionResult<GoalieApplicationDto>> Propose(
            Guid eventId,
            [FromQuery] Guid currentUserId,
            [FromBody] ProposeGoalieRequest request,
            CancellationToken cancellationToken)
        {
            var scheduledEvent = await _context.Events.AsNoTracking().FirstOrDefaultAsync(value => value.Id == eventId, cancellationToken);
            var goalieRequest = await LoadRequest(eventId);
            if (scheduledEvent == null || goalieRequest == null)
            {
                return NotFound(new { message = "Объявление не найдено." });
            }

            if (!scheduledEvent.TeamId.HasValue || !await CanManageTeam(scheduledEvent.TeamId.Value, currentUserId))
            {
                return Forbid();
            }

            var goalie = await _context.Users.AsNoTracking().FirstOrDefaultAsync(value => value.Id == request.GoalieUserId, cancellationToken);
            if (goalie == null)
            {
                return NotFound(new { message = "Вратарь не найден." });
            }

            if (goalie.PrimaryPosition != Position.Goalie)
            {
                return BadRequest(new { message = "Предложение можно отправить только вратарю." });
            }

            var existing = goalieRequest.Applications.FirstOrDefault(value => value.GoalieUserId == request.GoalieUserId);
            if (existing != null)
            {
                if (IsInactiveApplication(existing))
                {
                    existing.Status = GoalieApplicationStatus.Proposed;
                    existing.Source = GoalieApplicationSource.ManualProposal;
                    existing.Message = NormalizeText(request.Message, 1000);
                    existing.UpdatedAt = DateTime.UtcNow;
                    UpdateRequestStatus(goalieRequest);
                    await _context.SaveChangesAsync(cancellationToken);

                    await SendGoaliePush(
                        request.GoalieUserId,
                        "Вам предложили встать в ворота",
                        $"Событие: {scheduledEvent.Title}",
                        $"/events/{eventId}",
                        cancellationToken);
                }

                return Ok(await ToApplicationDto(existing, scheduledEvent.StartTime, eventId));
            }

            var application = new GoalieApplication
            {
                GoalieRequestId = goalieRequest.Id,
                GoalieUserId = request.GoalieUserId,
                Status = GoalieApplicationStatus.Proposed,
                Source = GoalieApplicationSource.ManualProposal,
                Message = NormalizeText(request.Message, 1000),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.GoalieApplications.AddAsync(application, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            await SendGoaliePush(
                request.GoalieUserId,
                "Вам предложили встать в ворота",
                $"Событие: {scheduledEvent.Title}",
                $"/events/{eventId}",
                cancellationToken);

            application = await _context.GoalieApplications
                .Include(value => value.GoalieUser)
                .FirstAsync(value => value.Id == application.Id, cancellationToken);
            return Ok(await ToApplicationDto(application, scheduledEvent.StartTime, eventId));
        }

        [HttpPost("applications/{applicationId:guid}/status")]
        public async Task<ActionResult<GoalieApplicationDto>> UpdateStatus(
            Guid eventId,
            Guid applicationId,
            [FromQuery] Guid currentUserId,
            [FromBody] UpdateGoalieApplicationStatusRequest request,
            CancellationToken cancellationToken)
        {
            var scheduledEvent = await _context.Events.AsNoTracking().FirstOrDefaultAsync(value => value.Id == eventId, cancellationToken);
            var application = await _context.GoalieApplications
                .Include(value => value.GoalieUser)
                .Include(value => value.GoalieRequest)
                    .ThenInclude(value => value.Applications)
                .FirstOrDefaultAsync(value => value.Id == applicationId && value.GoalieRequest.EventId == eventId, cancellationToken);
            if (scheduledEvent == null || application == null)
            {
                return NotFound(new { message = "Заявка не найдена." });
            }

            var isGoalieOwner = application.GoalieUserId == currentUserId;
            var canManage = scheduledEvent.TeamId.HasValue && await CanManageTeam(scheduledEvent.TeamId.Value, currentUserId);
            var adminStatuses = new[] { GoalieApplicationStatus.Accepted, GoalieApplicationStatus.Rejected };
            var goalieStatuses = new[] { GoalieApplicationStatus.Confirmed, GoalieApplicationStatus.Declined };

            if (request.Status == GoalieApplicationStatus.Cancelled)
            {
                if (!canManage && !isGoalieOwner)
                {
                    return Forbid();
                }
            }
            else if (adminStatuses.Contains(request.Status))
            {
                if (!canManage)
                {
                    return Forbid();
                }

                if (application.Status == GoalieApplicationStatus.Proposed)
                {
                    return BadRequest(new { message = "Личное предложение должен подтвердить или отклонить сам вратарь." });
                }
            }
            else if (goalieStatuses.Contains(request.Status))
            {
                if (!isGoalieOwner)
                {
                    return Forbid();
                }
            }
            else
            {
                return BadRequest(new { message = "Этот статус нельзя установить вручную." });
            }

            application.Status = request.Status;
            application.UpdatedAt = DateTime.UtcNow;
            UpdateRequestStatus(application.GoalieRequest);
            await _context.SaveChangesAsync(cancellationToken);

            if (request.Status == GoalieApplicationStatus.Accepted)
            {
                await SendGoaliePush(
                    application.GoalieUserId,
                    "Заявка принята",
                    $"Вас готовы взять на событие: {scheduledEvent.Title}",
                    $"/events/{eventId}",
                    cancellationToken);
            }

            return Ok(await ToApplicationDto(application, scheduledEvent.StartTime, eventId));
        }

        private async Task<GoalieRequest?> LoadRequest(Guid eventId)
        {
            return await _context.GoalieRequests
                .Include(value => value.Applications)
                    .ThenInclude(value => value.GoalieUser)
                .FirstOrDefaultAsync(value => value.EventId == eventId);
        }

        private async Task<bool> CanManageTeam(Guid teamId, Guid currentUserId)
        {
            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(value =>
                    value.TeamId == teamId &&
                    value.UserId == currentUserId &&
                    (value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin));
        }

        private static bool IsRequestVisible(GoalieRequest request, bool isGoalie, bool isTeamMember, bool canManage)
        {
            if (canManage)
            {
                return true;
            }

            if (isTeamMember)
            {
                return true;
            }

            if (!isGoalie)
            {
                return false;
            }

            return request.Visibility == GoalieRequestVisibility.AllGoalies || isTeamMember;
        }

        private async Task<List<GoalieUserDto>> LoadAvailableGoalies(ScheduledEvent scheduledEvent, GoalieRequest? request)
        {
            var query = _context.Users
                .AsNoTracking()
                .Where(value => value.PrimaryPosition == Position.Goalie);

            if (request?.Visibility != GoalieRequestVisibility.AllGoalies && scheduledEvent.TeamId.HasValue)
            {
                query = query.Where(value => _context.TeamMemberships.Any(member =>
                    member.TeamId == scheduledEvent.TeamId.Value && member.UserId == value.Id));
            }

            var existingGoalieIds = request?.Applications
                .Where(value => !IsInactiveApplication(value))
                .Select(value => value.GoalieUserId)
                .ToHashSet() ?? new HashSet<Guid>();
            var goalies = await query
                .OrderBy(value => value.LastName)
                .ThenBy(value => value.FirstName)
                .Take(100)
                .ToListAsync();

            var result = new List<GoalieUserDto>();
            foreach (var goalie in goalies.Where(value => !existingGoalieIds.Contains(value.Id)))
            {
                result.Add(new GoalieUserDto
                {
                    UserId = goalie.Id,
                    FirstName = goalie.FirstName,
                    LastName = goalie.LastName,
                    JerseyNumber = goalie.JerseyNumber,
                    PhotoUrl = goalie.PhotoUrl,
                    Conflict = await FindConflict(goalie.Id, scheduledEvent.StartTime, scheduledEvent.Id)
                });
            }

            return result;
        }

        private async Task<List<GoalieRequestDto>> LoadPreviousRequests(ScheduledEvent scheduledEvent, Guid currentEventId)
        {
            if (!scheduledEvent.TeamId.HasValue)
            {
                return new List<GoalieRequestDto>();
            }

            var previousRequests = await _context.GoalieRequests
                .AsNoTracking()
                .Include(value => value.Applications)
                    .ThenInclude(value => value.GoalieUser)
                .Include(value => value.Event)
                .Where(value => value.TeamId == scheduledEvent.TeamId.Value && value.EventId != currentEventId)
                .OrderByDescending(value => value.CreatedAt)
                .Take(8)
                .ToListAsync();

            var result = new List<GoalieRequestDto>();
            foreach (var request in previousRequests)
            {
                result.Add(await ToRequestDto(request, request.Event.StartTime, request.EventId));
            }

            return result;
        }

        private async Task<GoalieRequestDto> ToRequestDto(GoalieRequest request, DateTime eventStartTime, Guid currentEventId)
        {
            var applications = new List<GoalieApplicationDto>();
            foreach (var application in request.Applications.OrderBy(value => value.CreatedAt))
            {
                applications.Add(await ToApplicationDto(application, eventStartTime, currentEventId));
            }

            return new GoalieRequestDto
            {
                Id = request.Id,
                EventId = request.EventId,
                TeamId = request.TeamId,
                NeededCount = request.NeededCount,
                Visibility = request.Visibility,
                ResponseMode = request.ResponseMode,
                Status = request.Status,
                PriceText = request.PriceText,
                Description = request.Description,
                ConfirmedCount = request.Applications.Count(value => value.Status == GoalieApplicationStatus.Confirmed),
                CreatedAt = request.CreatedAt,
                UpdatedAt = request.UpdatedAt,
                Applications = applications
            };
        }

        private async Task<GoalieApplicationDto> ToApplicationDto(GoalieApplication application, DateTime eventStartTime, Guid currentEventId)
        {
            return new GoalieApplicationDto
            {
                Id = application.Id,
                UserId = application.GoalieUserId,
                FirstName = application.GoalieUser.FirstName,
                LastName = application.GoalieUser.LastName,
                JerseyNumber = application.GoalieUser.JerseyNumber,
                PhotoUrl = application.GoalieUser.PhotoUrl,
                Status = application.Status,
                Source = application.Source,
                Message = application.Message,
                CreatedAt = application.CreatedAt,
                UpdatedAt = application.UpdatedAt,
                Conflict = await FindConflict(application.GoalieUserId, eventStartTime, currentEventId)
            };
        }

        private async Task<GoalieEventConflictDto?> FindConflict(Guid goalieUserId, DateTime startTime, Guid currentEventId)
        {
            var windowStart = startTime - ConflictWindow;
            var windowEnd = startTime + ConflictWindow;

            return await _context.GoalieApplications
                .AsNoTracking()
                .Where(value =>
                    value.GoalieUserId == goalieUserId &&
                    value.Status == GoalieApplicationStatus.Confirmed &&
                    value.GoalieRequest.EventId != currentEventId &&
                    value.GoalieRequest.Event.StartTime >= windowStart &&
                    value.GoalieRequest.Event.StartTime <= windowEnd)
                .OrderBy(value => value.GoalieRequest.Event.StartTime)
                .Select(value => new GoalieEventConflictDto
                {
                    EventId = value.GoalieRequest.EventId,
                    Title = value.GoalieRequest.Event.Title,
                    StartTime = value.GoalieRequest.Event.StartTime
                })
                .FirstOrDefaultAsync();
        }

        private static void UpdateRequestStatus(GoalieRequest request)
        {
            if (request.Status == GoalieRequestStatus.Closed)
            {
                return;
            }

            var confirmedCount = request.Applications.Count(value => value.Status == GoalieApplicationStatus.Confirmed);
            request.Status = confirmedCount >= request.NeededCount
                ? GoalieRequestStatus.Filled
                : GoalieRequestStatus.Open;
            request.UpdatedAt = DateTime.UtcNow;
        }

        private static bool IsInactiveApplication(GoalieApplication application)
        {
            return application.Status == GoalieApplicationStatus.Rejected ||
                application.Status == GoalieApplicationStatus.Declined ||
                application.Status == GoalieApplicationStatus.Cancelled;
        }

        private async Task SendGoaliePush(Guid goalieUserId, string title, string body, string url, CancellationToken cancellationToken)
        {
            if (!_webPushService.IsConfigured)
            {
                return;
            }

            var subscriptions = await _context.PushSubscriptions
                .Where(value => value.UserId == goalieUserId)
                .ToListAsync(cancellationToken);

            foreach (var subscription in subscriptions)
            {
                var result = await _webPushService.SendAsync(subscription, new { title, body, url }, cancellationToken);
                if (result.ShouldRemoveSubscription)
                {
                    _context.PushSubscriptions.Remove(subscription);
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Goalie push sent to {Count} subscriptions for user {UserId}", subscriptions.Count, goalieUserId);
        }

        private static string? NormalizeText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim();
            return normalized.Length > maxLength ? normalized[..maxLength] : normalized;
        }
    }
}
