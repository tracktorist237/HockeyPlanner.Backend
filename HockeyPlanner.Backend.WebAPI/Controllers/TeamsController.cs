using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using Microsoft.AspNetCore.Authorization;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Teams;
using HockeyPlanner.Backend.Shared.Models.Teams;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/teams")]
    public class TeamsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ICoreTeamService _coreTeamService;
        private readonly ICurrentUser _currentUser;
        private readonly INotificationService _notificationService;
        private readonly IFileStorageService _fileStorageService;
        private readonly ITeamPwaService _teamPwaService;
        private readonly ILogger<TeamsController> _logger;

        public TeamsController(
            AppDbContext context,
            ICoreTeamService coreTeamService,
            ICurrentUser currentUser,
            INotificationService notificationService,
            IFileStorageService fileStorageService,
            ITeamPwaService teamPwaService,
            ILogger<TeamsController> logger)
        {
            _context = context;
            _coreTeamService = coreTeamService;
            _currentUser = currentUser;
            _notificationService = notificationService;
            _fileStorageService = fileStorageService;
            _teamPwaService = teamPwaService;
            _logger = logger;
        }

        [AllowAnonymous]
        [HttpGet("{id:guid}/pwa-logo")]
        public async Task<IActionResult> GetPwaLogo(Guid id, CancellationToken cancellationToken)
        {
            var result = await _teamPwaService.GetOriginalLogoAsync(id, cancellationToken);
            if (result == null)
            {
                return NotFound(new { message = "Команда или поддерживаемый логотип команды не найдены." });
            }

            Response.Headers.CacheControl = "public, max-age=3600, must-revalidate";
            Response.Headers.ETag = result.EntityTag;
            return File(result.Content, result.ContentType);
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<TeamDto>>> GetMyTeams()
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.GetMyTeams(actorUserId, HttpContext.RequestAborted));
        }

        [AllowAnonymous]
        [HttpGet("public")]
        public async Task<ActionResult<IReadOnlyCollection<TeamDto>>> GetPublicTeams()
        {
            return Ok(await _coreTeamService.GetPublicTeams(HttpContext.RequestAborted));
        }

        [AllowAnonymous]
        [HttpGet("{id:guid}")]
        public async Task<ActionResult<TeamDto>> GetTeam(Guid id)
        {
            return Ok(await _coreTeamService.GetTeam(id, _currentUser.IsAuthenticated, _currentUser.UserId, HttpContext.RequestAborted));
        }

        [AllowAnonymous]
        [HttpGet("{id:guid}/members")]
        public async Task<ActionResult<IReadOnlyCollection<TeamMemberDto>>> GetTeamMembers(Guid id)
        {
            return Ok(await _coreTeamService.GetTeamMembers(id, _currentUser.IsAuthenticated, _currentUser.UserId, HttpContext.RequestAborted));
        }

        [AllowAnonymous]
        [HttpGet("{id:guid}/news")]
        public async Task<ActionResult<IReadOnlyCollection<TeamNewsDto>>> GetTeamNews(Guid id)
        {
            var actorUserId = _currentUser.UserId;
            var team = await _context.Teams.AsNoTracking()
                .Include(value => value.Memberships)
                .FirstOrDefaultAsync(value => value.Id == id, HttpContext.RequestAborted);
            if (team == null)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            var visibilityError = CheckTeamVisibility(team, actorUserId);
            if (visibilityError != null) return visibilityError;

            var canManage = actorUserId.HasValue && actorUserId.Value != Guid.Empty && await CanManageTeamAsync(id, actorUserId.Value);

            var news = await _context.TeamNews
                .AsNoTracking()
                .Where(value => value.TeamId == id)
                .OrderByDescending(value => value.CreatedAt)
                .Take(50)
                .Select(value => new TeamNewsDto
                {
                    Id = value.Id,
                    TeamId = value.TeamId,
                    TeamName = value.Team.Name,
                    Title = value.Title,
                    Body = value.Body,
                    ImageUrl = value.ImageUrl,
                    AuthorUserId = value.AuthorUserId,
                    AuthorName = (value.AuthorUser.LastName + " " + value.AuthorUser.FirstName).Trim(),
                    CreatedAt = value.CreatedAt,
                    UpdatedAt = value.UpdatedAt,
                    CanManage = canManage
                })
                .ToListAsync();

            return Ok(news);
        }

        [HttpGet("~/api/news")]
        public async Task<ActionResult<IReadOnlyCollection<TeamNewsDto>>> GetNewsFeed()
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == actorUserId);
            if (!userExists)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var manageableTeamIds = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.UserId == actorUserId && (value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin))
                .Select(value => value.TeamId)
                .ToListAsync();

            var news = await _context.TeamNews
                .AsNoTracking()
                .Where(value => value.Team.Memberships.Any(membership => membership.UserId == actorUserId))
                .OrderByDescending(value => value.CreatedAt)
                .Take(100)
                .Select(value => new TeamNewsDto
                {
                    Id = value.Id,
                    TeamId = value.TeamId,
                    TeamName = value.Team.Name,
                    Title = value.Title,
                    Body = value.Body,
                    ImageUrl = value.ImageUrl,
                    AuthorUserId = value.AuthorUserId,
                    AuthorName = (value.AuthorUser.LastName + " " + value.AuthorUser.FirstName).Trim(),
                    CreatedAt = value.CreatedAt,
                    UpdatedAt = value.UpdatedAt,
                    CanManage = manageableTeamIds.Contains(value.TeamId)
                })
                .ToListAsync();

            return Ok(news);
        }

        [HttpPost("{id:guid}/news")]
        public async Task<ActionResult<TeamNewsDto>> CreateTeamNews(
            Guid id,
            [FromBody] CreateTeamNewsRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            var title = NormalizeNewsTitle(request.Title);
            var body = NormalizeNewsBody(request.Body);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
            {
                return BadRequest(new { message = "У новости должны быть название и текст." });
            }

            var membership = await _context.TeamMemberships
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == actorUserId);

            if (membership == null || (membership.Role != TeamMemberRole.Owner && membership.Role != TeamMemberRole.Admin))
            {
                return Forbid();
            }

            var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(value => value.Id == actorUserId);
            if (user == null)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var news = new TeamNews
            {
                TeamId = id,
                AuthorUserId = actorUserId,
                Title = title,
                Body = body,
                ImageUrl = NormalizeUrl(request.ImageUrl),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.TeamNews.AddAsync(news);

            if (request.SendNotification)
            {
                await _notificationService.NotifyTeamAsync(
                    id,
                    NotificationType.TeamNewsCreated,
                    NotificationCategory.TeamNews,
                    title,
                    body,
                    $"/teams/{id}", HttpContext.RequestAborted);
            }

            await _context.SaveChangesAsync(HttpContext.RequestAborted);
            return Ok(new TeamNewsDto
            {
                Id = news.Id,
                TeamId = news.TeamId,
                TeamName = string.Empty,
                Title = news.Title,
                Body = news.Body,
                ImageUrl = news.ImageUrl,
                AuthorUserId = news.AuthorUserId,
                AuthorName = $"{user.LastName} {user.FirstName}".Trim(),
                CreatedAt = news.CreatedAt,
                UpdatedAt = news.UpdatedAt,
                CanManage = true
            });
        }

        [HttpPut("{teamId:guid}/news/{newsId:guid}")]
        public async Task<ActionResult<TeamNewsDto>> UpdateTeamNews(
            Guid teamId,
            Guid newsId,
            [FromBody] UpdateTeamNewsRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            if (!await CanManageTeamAsync(teamId, actorUserId))
            {
                return Forbid();
            }

            var title = NormalizeNewsTitle(request.Title);
            var body = NormalizeNewsBody(request.Body);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
            {
                return BadRequest(new { message = "У новости должны быть название и текст." });
            }

            var news = await _context.TeamNews
                .Include(value => value.Team)
                .Include(value => value.AuthorUser)
                .FirstOrDefaultAsync(value => value.Id == newsId && value.TeamId == teamId);

            if (news == null)
            {
                return NotFound(new { message = "Новость не найдена." });
            }

            news.Title = title;
            news.Body = body;
            news.ImageUrl = NormalizeUrl(request.ImageUrl);
            news.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new TeamNewsDto
            {
                Id = news.Id,
                TeamId = news.TeamId,
                TeamName = news.Team.Name,
                Title = news.Title,
                Body = news.Body,
                ImageUrl = news.ImageUrl,
                AuthorUserId = news.AuthorUserId,
                AuthorName = $"{news.AuthorUser.LastName} {news.AuthorUser.FirstName}".Trim(),
                CreatedAt = news.CreatedAt,
                UpdatedAt = news.UpdatedAt,
                CanManage = true
            });
        }

        [HttpDelete("{teamId:guid}/news/{newsId:guid}")]
        public async Task<IActionResult> DeleteTeamNews(Guid teamId, Guid newsId)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            if (!await CanManageTeamAsync(teamId, actorUserId))
            {
                return Forbid();
            }

            var news = await _context.TeamNews.FirstOrDefaultAsync(value => value.Id == newsId && value.TeamId == teamId);
            if (news == null)
            {
                return NotFound(new { message = "Новость не найдена." });
            }

            _context.TeamNews.Remove(news);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpPost("{id:guid}/avatar/upload")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<TeamDto>> UploadTeamAvatar(
            Guid id,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return await UploadTeamMedia(id, actorUserId, file, isCover: false, cancellationToken);
        }

        [HttpPost("{id:guid}/cover/upload")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<TeamDto>> UploadTeamCover(
            Guid id,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return await UploadTeamMedia(id, actorUserId, file, isCover: true, cancellationToken);
        }

        [HttpPost("{id:guid}/news/upload-image")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<UploadTeamImageResponse>> UploadTeamNewsImage(
            Guid id,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            if (!await CanManageTeamAsync(id, actorUserId))
            {
                return Forbid();
            }

            var validation = ValidateImageFile(file);
            if (validation != null)
            {
                return BadRequest(new { message = validation });
            }

            try
            {
                await using var stream = file.OpenReadStream();
                var uploadResult = await _fileStorageService.UploadAsync(
                    new FileStorageUploadRequest
                    {
                        Content = stream,
                        FileName = file.FileName,
                        ContentType = file.ContentType,
                        Folder = FileStorageFolders.News,
                        ScopeId = id.ToString("N")
                    },
                    cancellationToken);

                return Ok(new UploadTeamImageResponse { ImageUrl = uploadResult.PublicUrl });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Не удалось загрузить изображение новости." });
            }
        }

        [HttpPost]
        public async Task<ActionResult<TeamDto>> CreateTeam([FromBody] CreateTeamRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            var dto = await _coreTeamService.CreateTeam(actorUserId, request, HttpContext.RequestAborted);
            return CreatedAtAction(nameof(GetTeam), new { id = dto.Id }, dto);
        }

        [HttpPut("{id:guid}")]
        public async Task<ActionResult<TeamDto>> UpdateTeam(Guid id, [FromBody] UpdateTeamRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.UpdateTeam(id, actorUserId, request, HttpContext.RequestAborted));
        }

        [HttpPut("{id:guid}/members/{userId:guid}")]
        public async Task<ActionResult<TeamMemberDto>> UpdateTeamMember(
            Guid id,
            Guid userId,
            [FromBody] UpdateTeamMemberRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.UpdateTeamMember(id, userId, actorUserId, request, HttpContext.RequestAborted));
        }

        [HttpDelete("{id:guid}/members/{userId:guid}")]
        public async Task<IActionResult> RemoveTeamMember(Guid id, Guid userId)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            await _coreTeamService.RemoveTeamMember(id, userId, actorUserId, HttpContext.RequestAborted);
            return NoContent();
        }

        [HttpPost("join-by-code")]
        public async Task<ActionResult<TeamDto>> JoinByCode([FromBody] JoinTeamByCodeRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.JoinByCode(actorUserId, request, HttpContext.RequestAborted));
        }

        [HttpPost("{id:guid}/join-public")]
        public async Task<ActionResult<TeamDto>> JoinPublic(Guid id, [FromQuery] int? teamJerseyNumber)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.JoinPublic(id, actorUserId, teamJerseyNumber, HttpContext.RequestAborted));
        }

        [HttpDelete("{id:guid}/members/me")]
        public async Task<IActionResult> LeaveTeam(Guid id)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            await _coreTeamService.LeaveTeam(id, actorUserId, HttpContext.RequestAborted);
            return NoContent();
        }

        private async Task<ActionResult<TeamDto>> UploadTeamMedia(
            Guid id,
            Guid actorUserId,
            IFormFile file,
            bool isCover,
            CancellationToken cancellationToken)
        {
            var team = await _context.Teams
                .Include(value => value.Memberships)
                .FirstOrDefaultAsync(value => value.Id == id, cancellationToken);

            if (team == null)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            var actorMembership = team.Memberships.FirstOrDefault(value => value.UserId == actorUserId);
            if (actorMembership == null ||
                (actorMembership.Role != TeamMemberRole.Owner && actorMembership.Role != TeamMemberRole.Admin))
            {
                return Forbid();
            }

            var validation = ValidateImageFile(file);
            if (validation != null)
            {
                return BadRequest(new { message = validation });
            }

            try
            {
                await using var stream = file.OpenReadStream();
                var uploadResult = await _fileStorageService.UploadAsync(
                    new FileStorageUploadRequest
                    {
                        Content = stream,
                        FileName = file.FileName,
                        ContentType = file.ContentType,
                        Folder = FileStorageFolders.Teams,
                        ScopeId = id.ToString("N")
                    },
                    cancellationToken);

                if (isCover)
                {
                    team.CoverImageUrl = uploadResult.PublicUrl;
                }
                else
                {
                    team.AvatarUrl = uploadResult.PublicUrl;
                }

                team.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

                return Ok(ToDto(team, actorMembership.Role, actorMembership.BadgeTitle, team.InviteCode));
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Не удалось загрузить изображение команды." });
            }
        }

        private static string NormalizeName(string value)
        {
            var parts = value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(" ", parts);
        }

        private static string? NormalizeUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim();
            return normalized.Length > 500 ? normalized[..500] : normalized;
        }

        private static string? ValidateImageFile(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return "Файл изображения не передан.";
            }

            if (file.Length > 5 * 1024 * 1024)
            {
                return "Размер файла не должен превышать 5 МБ.";
            }

            if (string.IsNullOrWhiteSpace(file.ContentType) ||
                !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return "Нужен файл изображения.";
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var allowedExtensions = new HashSet<string> { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
            if (!allowedExtensions.Contains(extension))
            {
                return "Поддерживаются форматы JPG, PNG, WEBP, GIF.";
            }

            return null;
        }

        // Optional-auth reads use only the validated JWT viewer, never compatibility query data.
        private ActionResult? CheckTeamVisibility(Team team, Guid? viewerUserId)
        {
            if (_currentUser.IsAuthenticated && (!viewerUserId.HasValue || viewerUserId == Guid.Empty))
            {
                return Unauthorized();
            }

            if (team.Visibility == TeamVisibility.Public) return null;
            if (!viewerUserId.HasValue) return Unauthorized();
            return team.Memberships.Any(member => member.UserId == viewerUserId) ? null : Forbid();
        }

        private async Task<bool> CanManageTeamAsync(Guid teamId, Guid userId)
        {
            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(value =>
                    value.TeamId == teamId &&
                    value.UserId == userId &&
                    (value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin));
        }

        [HttpPut("{id:guid}/members/me/number")]
        public async Task<ActionResult<TeamDto>> UpdateMyTeamJerseyNumber(
            Guid id,
            [FromBody] UpdateMyTeamJerseyNumberRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.UpdateMyTeamJerseyNumber(id, actorUserId, request, HttpContext.RequestAborted));
        }

        private static string NormalizeNewsTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = NormalizeName(value);
            return normalized.Length > 120 ? normalized[..120] : normalized;
        }

        private static string NormalizeNewsBody(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            return normalized.Length > 2000 ? normalized[..2000] : normalized;
        }

        private static TeamDto ToDto(
            Team team,
            TeamMemberRole? myRole,
            string? myBadgeTitle,
            string inviteCode,
            int? membersCount = null,
            int? myTeamJerseyNumber = null)
        {
            return new TeamDto
            {
                Id = team.Id,
                Name = team.Name,
                Description = team.Description,
                AvatarUrl = team.AvatarUrl,
                CoverImageUrl = team.CoverImageUrl,
                Phones = DeserializeContacts(team.PhoneContactsJson),
                Links = DeserializeContacts(team.LinkContactsJson),
                Addresses = DeserializeContacts(team.AddressContactsJson),
                Visibility = team.Visibility,
                InviteCode = inviteCode,
                CreatedByUserId = team.CreatedByUserId,
                MembersCount = membersCount ?? team.Memberships.Count,
                MyRole = myRole,
                MyBadgeTitle = myBadgeTitle,
                MyTeamJerseyNumber = myTeamJerseyNumber,
                AllowDuplicateJerseyNumbers = team.AllowDuplicateJerseyNumbers,
                BlockedJerseyNumbers = DeserializeJerseyNumbers(team.BlockedJerseyNumbersJson)
            };
        }

        private static List<int> NormalizeJerseyNumbers(IEnumerable<int>? values) =>
            (values ?? Array.Empty<int>()).Where(value => value >= 0 && value <= 99).Distinct().OrderBy(value => value).ToList();

        private static IReadOnlyCollection<int> DeserializeJerseyNumbers(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<int>();
            try { return NormalizeJerseyNumbers(JsonSerializer.Deserialize<List<int>>(value)); }
            catch (JsonException) { return Array.Empty<int>(); }
        }

        private static IReadOnlyCollection<TeamContactItemDto> DeserializeContacts(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<TeamContactItemDto>();
            }

            try
            {
                return NormalizeContacts(JsonSerializer.Deserialize<List<TeamContactItemDto>>(value)).ToList();
            }
            catch (JsonException)
            {
                return Array.Empty<TeamContactItemDto>();
            }
        }

        private static IEnumerable<TeamContactItemDto> NormalizeContacts(IEnumerable<TeamContactItemDto>? contacts)
        {
            return (contacts ?? Array.Empty<TeamContactItemDto>())
                .Select(contact => new TeamContactItemDto
                {
                    Title = NormalizeName(contact.Title ?? string.Empty),
                    Value = (contact.Value ?? string.Empty).Trim()
                })
                .Where(contact => !string.IsNullOrWhiteSpace(contact.Title) && !string.IsNullOrWhiteSpace(contact.Value))
                .Take(10)
                .Select(contact => new TeamContactItemDto
                {
                    Title = contact.Title.Length > 80 ? contact.Title[..80] : contact.Title,
                    Value = contact.Value.Length > 500 ? contact.Value[..500] : contact.Value
                });
        }
    }
}
