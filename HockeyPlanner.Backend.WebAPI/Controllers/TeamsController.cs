using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Teams;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/teams")]
    public class TeamsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly INotificationService _notificationService;
        private readonly IFileStorageService _fileStorageService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TeamsController> _logger;

        public TeamsController(
            AppDbContext context,
            INotificationService notificationService,
            IFileStorageService fileStorageService,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<TeamsController> logger)
        {
            _context = context;
            _notificationService = notificationService;
            _fileStorageService = fileStorageService;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("{id:guid}/pwa-logo")]
        public async Task<IActionResult> GetPwaLogo(Guid id, CancellationToken cancellationToken)
        {
            var avatarUrl = await _context.Teams
                .AsNoTracking()
                .Where(value => value.Id == id)
                .Select(value => value.AvatarUrl)
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(avatarUrl))
            {
                return NotFound(new { message = "У команды не настроен логотип." });
            }

            if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var avatarUri) ||
                avatarUri.Scheme != Uri.UriSchemeHttps ||
                !IsAllowedPwaLogoSource(avatarUri))
            {
                return BadRequest(new { message = "Источник логотипа команды не поддерживается." });
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                using var response = await client.GetAsync(
                    avatarUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode(StatusCodes.Status502BadGateway, new { message = "Не удалось получить логотип команды." });
                }

                const int maxLogoBytes = 5 * 1024 * 1024;
                if (response.Content.Headers.ContentLength > maxLogoBytes)
                {
                    return BadRequest(new { message = "Логотип команды слишком большой." });
                }

                await response.Content.LoadIntoBufferAsync(maxLogoBytes, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "Файл логотипа не является изображением." });
                }

                Response.Headers.CacheControl = "public, max-age=3600";
                return File(bytes, contentType);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to proxy PWA logo for team {TeamId}", id);
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Не удалось подготовить логотип приложения." });
            }
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<TeamDto>>> GetMyTeams([FromQuery] Guid currentUserId)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == currentUserId);
            if (!userExists)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var teams = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.UserId == currentUserId)
                .OrderBy(value => value.Team.Name)
                .Select(value => new TeamDto
                {
                    Id = value.Team.Id,
                    Name = value.Team.Name,
                    Description = value.Team.Description,
                    AvatarUrl = value.Team.AvatarUrl,
                    CoverImageUrl = value.Team.CoverImageUrl,
                    Visibility = value.Team.Visibility,
                    InviteCode = value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin
                        ? value.Team.InviteCode
                        : string.Empty,
                    CreatedByUserId = value.Team.CreatedByUserId,
                    MembersCount = value.Team.Memberships.Count,
                    MyRole = value.Role,
                    MyBadgeTitle = value.BadgeTitle
                })
                .ToListAsync();

            return Ok(teams);
        }

        [HttpGet("public")]
        public async Task<ActionResult<IReadOnlyCollection<TeamDto>>> GetPublicTeams()
        {
            var teams = await _context.Teams
                .AsNoTracking()
                .Where(team => team.Visibility == TeamVisibility.Public)
                .OrderBy(team => team.Name)
                .Select(team => new TeamDto
                {
                    Id = team.Id,
                    Name = team.Name,
                    Description = team.Description,
                    AvatarUrl = team.AvatarUrl,
                    CoverImageUrl = team.CoverImageUrl,
                    Visibility = team.Visibility,
                    InviteCode = string.Empty,
                    CreatedByUserId = team.CreatedByUserId,
                    MembersCount = team.Memberships.Count
                })
                .ToListAsync();

            return Ok(teams);
        }

        [HttpGet("{id:guid}")]
        public async Task<ActionResult<TeamDto>> GetTeam(Guid id, [FromQuery] Guid? currentUserId)
        {
            var team = await _context.Teams
                .AsNoTracking()
                .Include(value => value.Memberships)
                .Where(value => value.Id == id)
                .FirstOrDefaultAsync();

            if (team == null)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            var membership = currentUserId.HasValue
                ? team.Memberships.FirstOrDefault(member => member.UserId == currentUserId)
                : null;
            var canSeeInvite = membership?.Role == TeamMemberRole.Owner || membership?.Role == TeamMemberRole.Admin;

            return Ok(ToDto(team, membership?.Role, membership?.BadgeTitle, canSeeInvite ? team.InviteCode : string.Empty));
        }

        [HttpGet("{id:guid}/members")]
        public async Task<ActionResult<IReadOnlyCollection<TeamMemberDto>>> GetTeamMembers(Guid id)
        {
            var teamExists = await _context.Teams.AsNoTracking().AnyAsync(team => team.Id == id);
            if (!teamExists)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            var members = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.TeamId == id)
                .OrderBy(value => value.Role)
                .ThenBy(value => value.User.LastName)
                .ThenBy(value => value.User.FirstName)
                .Select(value => new TeamMemberDto
                {
                    UserId = value.UserId,
                    FirstName = value.User.FirstName,
                    LastName = value.User.LastName,
                    JerseyNumber = value.User.JerseyNumber,
                    PhotoUrl = value.User.PhotoUrl,
                    Role = value.Role,
                    BadgeTitle = value.BadgeTitle
                })
                .ToListAsync();

            return Ok(members);
        }

        [HttpGet("{id:guid}/news")]
        public async Task<ActionResult<IReadOnlyCollection<TeamNewsDto>>> GetTeamNews(Guid id, [FromQuery] Guid? currentUserId)
        {
            var teamExists = await _context.Teams.AsNoTracking().AnyAsync(team => team.Id == id);
            if (!teamExists)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            var canManage = currentUserId.HasValue && currentUserId.Value != Guid.Empty && await CanManageTeamAsync(id, currentUserId.Value);

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
        public async Task<ActionResult<IReadOnlyCollection<TeamNewsDto>>> GetNewsFeed([FromQuery] Guid currentUserId)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == currentUserId);
            if (!userExists)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var manageableTeamIds = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.UserId == currentUserId && (value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin))
                .Select(value => value.TeamId)
                .ToListAsync();

            var news = await _context.TeamNews
                .AsNoTracking()
                .Where(value => value.Team.Memberships.Any(membership => membership.UserId == currentUserId))
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
            [FromQuery] Guid currentUserId,
            [FromBody] CreateTeamNewsRequest request)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var title = NormalizeNewsTitle(request.Title);
            var body = NormalizeNewsBody(request.Body);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
            {
                return BadRequest(new { message = "У новости должны быть название и текст." });
            }

            var membership = await _context.TeamMemberships
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == currentUserId);

            if (membership == null || (membership.Role != TeamMemberRole.Owner && membership.Role != TeamMemberRole.Admin))
            {
                return Forbid();
            }

            var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(value => value.Id == currentUserId);
            if (user == null)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var news = new TeamNews
            {
                TeamId = id,
                AuthorUserId = currentUserId,
                Title = title,
                Body = body,
                ImageUrl = NormalizeUrl(request.ImageUrl),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.TeamNews.AddAsync(news);
            await _context.SaveChangesAsync();

            if (request.SendNotification)
            {
                await _notificationService.NotifyTeamAsync(
                    id,
                    NotificationType.TeamNewsCreated,
                    NotificationCategory.TeamNews,
                    title,
                    body,
                    $"/teams/{id}");
            }

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
            [FromQuery] Guid currentUserId,
            [FromBody] UpdateTeamNewsRequest request)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            if (!await CanManageTeamAsync(teamId, currentUserId))
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
        public async Task<IActionResult> DeleteTeamNews(Guid teamId, Guid newsId, [FromQuery] Guid currentUserId)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            if (!await CanManageTeamAsync(teamId, currentUserId))
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
            [FromQuery] Guid currentUserId,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            return await UploadTeamMedia(id, currentUserId, file, isCover: false, cancellationToken);
        }

        [HttpPost("{id:guid}/cover/upload")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<TeamDto>> UploadTeamCover(
            Guid id,
            [FromQuery] Guid currentUserId,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            return await UploadTeamMedia(id, currentUserId, file, isCover: true, cancellationToken);
        }

        [HttpPost("{id:guid}/news/upload-image")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<UploadTeamImageResponse>> UploadTeamNewsImage(
            Guid id,
            [FromQuery] Guid currentUserId,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            if (!await CanManageTeamAsync(id, currentUserId))
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Team news image upload failed for team {TeamId}", id);
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Не удалось загрузить изображение новости." });
            }
        }

        [HttpPost]
        public async Task<ActionResult<TeamDto>> CreateTeam([FromBody] CreateTeamRequest request, [FromQuery] Guid currentUserId)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { message = "Название команды обязательно." });
            }

            var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == currentUserId);
            if (!userExists)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var normalizedName = NormalizeName(request.Name);

            var duplicateExists = await _context.Teams
                .AsNoTracking()
                .AnyAsync(team => team.Name.ToLower() == normalizedName.ToLower());

            if (duplicateExists)
            {
                return Conflict(new { message = "Команда с таким названием уже существует." });
            }

            var inviteCode = await GenerateUniqueInviteCode();

            var team = new Team
            {
                Name = normalizedName,
                Description = NormalizeDescription(request.Description),
                AvatarUrl = NormalizeUrl(request.AvatarUrl),
                CoverImageUrl = NormalizeUrl(request.CoverImageUrl),
                PhoneContactsJson = SerializeContacts(request.Phones),
                LinkContactsJson = SerializeContacts(request.Links),
                AddressContactsJson = SerializeContacts(request.Addresses),
                Visibility = request.Visibility,
                InviteCode = inviteCode,
                CreatedByUserId = currentUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var ownerMembership = new TeamMembership
            {
                Team = team,
                UserId = currentUserId,
                Role = TeamMemberRole.Owner,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.Teams.AddAsync(team);
            await _context.TeamMemberships.AddAsync(ownerMembership);
            await _context.SaveChangesAsync();

            var dto = ToDto(team, TeamMemberRole.Owner, ownerMembership.BadgeTitle, team.InviteCode, 1);

            return CreatedAtAction(nameof(GetTeam), new { id = team.Id, currentUserId }, dto);
        }

        [HttpPut("{id:guid}")]
        public async Task<ActionResult<TeamDto>> UpdateTeam(Guid id, [FromQuery] Guid currentUserId, [FromBody] UpdateTeamRequest request)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { message = "Название команды обязательно." });
            }

            var team = await _context.Teams
                .Include(value => value.Memberships)
                .FirstOrDefaultAsync(value => value.Id == id);

            if (team == null)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            var actorMembership = team.Memberships.FirstOrDefault(value => value.UserId == currentUserId);
            if (actorMembership == null ||
                (actorMembership.Role != TeamMemberRole.Owner && actorMembership.Role != TeamMemberRole.Admin))
            {
                return Forbid();
            }

            var normalizedName = NormalizeName(request.Name);
            var duplicateExists = await _context.Teams
                .AsNoTracking()
                .AnyAsync(value => value.Id != id && value.Name.ToLower() == normalizedName.ToLower());

            if (duplicateExists)
            {
                return Conflict(new { message = "Команда с таким названием уже существует." });
            }

            team.Name = normalizedName;
            team.Description = NormalizeDescription(request.Description);
            team.AvatarUrl = NormalizeUrl(request.AvatarUrl);
            team.CoverImageUrl = NormalizeUrl(request.CoverImageUrl);
            team.PhoneContactsJson = SerializeContacts(request.Phones);
            team.LinkContactsJson = SerializeContacts(request.Links);
            team.AddressContactsJson = SerializeContacts(request.Addresses);
            team.Visibility = request.Visibility;
            team.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(ToDto(team, actorMembership.Role, actorMembership.BadgeTitle, team.InviteCode));
        }

        [HttpPut("{id:guid}/members/{userId:guid}")]
        public async Task<ActionResult<TeamMemberDto>> UpdateTeamMember(
            Guid id,
            Guid userId,
            [FromQuery] Guid currentUserId,
            [FromBody] UpdateTeamMemberRequest request)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var actorMembership = await _context.TeamMemberships
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == currentUserId);

            if (actorMembership == null)
            {
                return Forbid();
            }

            if (actorMembership.Role != TeamMemberRole.Owner && actorMembership.Role != TeamMemberRole.Admin)
            {
                return Forbid();
            }

            var targetMembership = await _context.TeamMemberships
                .Include(value => value.User)
                .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == userId);

            if (targetMembership == null)
            {
                return NotFound(new { message = "Участник команды не найден." });
            }

            if (request.Role.HasValue && request.Role.Value != targetMembership.Role)
            {
                if (actorMembership.Role != TeamMemberRole.Owner)
                {
                    return Forbid();
                }

                if (targetMembership.Role == TeamMemberRole.Owner)
                {
                    return BadRequest(new { message = "Нельзя изменить роль владельца команды." });
                }

                if (request.Role.Value == TeamMemberRole.Owner)
                {
                    return BadRequest(new { message = "Передача владения пока не поддерживается." });
                }

                targetMembership.Role = request.Role.Value;
            }

            targetMembership.BadgeTitle = NormalizeBadgeTitle(request.BadgeTitle);
            targetMembership.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new TeamMemberDto
            {
                UserId = targetMembership.UserId,
                FirstName = targetMembership.User.FirstName,
                LastName = targetMembership.User.LastName,
                JerseyNumber = targetMembership.User.JerseyNumber,
                PhotoUrl = targetMembership.User.PhotoUrl,
                Role = targetMembership.Role,
                BadgeTitle = targetMembership.BadgeTitle
            });
        }

        [HttpDelete("{id:guid}/members/{userId:guid}")]
        public async Task<IActionResult> RemoveTeamMember(Guid id, Guid userId, [FromQuery] Guid currentUserId)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            if (userId == currentUserId)
            {
                return BadRequest(new { message = "Для выхода из команды используйте действие покинуть команду." });
            }

            var actorMembership = await _context.TeamMemberships
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == currentUserId);

            if (actorMembership == null ||
                (actorMembership.Role != TeamMemberRole.Owner && actorMembership.Role != TeamMemberRole.Admin))
            {
                return Forbid();
            }

            var targetMembership = await _context.TeamMemberships
                .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == userId);

            if (targetMembership == null)
            {
                return NotFound(new { message = "Участник команды не найден." });
            }

            if (targetMembership.Role == TeamMemberRole.Owner)
            {
                return BadRequest(new { message = "Нельзя удалить владельца команды." });
            }

            if (actorMembership.Role == TeamMemberRole.Admin && targetMembership.Role != TeamMemberRole.Member)
            {
                return Forbid();
            }

            _context.TeamMemberships.Remove(targetMembership);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpPost("join-by-code")]
        public async Task<ActionResult<TeamDto>> JoinByCode([FromBody] JoinTeamByCodeRequest request, [FromQuery] Guid currentUserId)
        {
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                return BadRequest(new { message = "Код приглашения обязателен." });
            }

            var normalizedCode = request.Code.Trim().ToUpperInvariant();

            var team = await _context.Teams
                .Include(value => value.Memberships)
                .FirstOrDefaultAsync(value => value.InviteCode == normalizedCode);

            if (team == null)
            {
                return NotFound(new { message = "Команда с таким кодом не найдена." });
            }

            return await JoinTeamInternal(team, currentUserId);
        }

        [HttpPost("{id:guid}/join-public")]
        public async Task<ActionResult<TeamDto>> JoinPublic(Guid id, [FromQuery] Guid currentUserId)
        {
            var team = await _context.Teams
                .Include(value => value.Memberships)
                .FirstOrDefaultAsync(value => value.Id == id);

            if (team == null)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            if (team.Visibility != TeamVisibility.Public)
            {
                return BadRequest(new { message = "В приватную команду можно вступить только по коду." });
            }

            return await JoinTeamInternal(team, currentUserId);
        }

        [HttpDelete("{id:guid}/members/me")]
        public async Task<IActionResult> LeaveTeam(Guid id, [FromQuery] Guid currentUserId)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var membership = await _context.TeamMemberships
                .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == currentUserId);

            if (membership == null)
            {
                return NotFound(new { message = "Вы не состоите в этой команде." });
            }

            if (membership.Role == TeamMemberRole.Owner)
            {
                var hasOtherMembers = await _context.TeamMemberships
                    .AsNoTracking()
                    .AnyAsync(value => value.TeamId == id && value.UserId != currentUserId);

                if (hasOtherMembers)
                {
                    return BadRequest(new { message = "Владелец не может покинуть команду, пока в ней есть другие участники." });
                }
            }

            _context.TeamMemberships.Remove(membership);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        private async Task<ActionResult<TeamDto>> JoinTeamInternal(Team team, Guid currentUserId)
        {
            var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == currentUserId);
            if (!userExists)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var alreadyMember = team.Memberships.Any(value => value.UserId == currentUserId);
            if (alreadyMember)
            {
                return Ok(new TeamDto
                {
                    Id = team.Id,
                    Name = team.Name,
                    Description = team.Description,
                    AvatarUrl = team.AvatarUrl,
                    CoverImageUrl = team.CoverImageUrl,
                    Visibility = team.Visibility,
                    InviteCode = string.Empty,
                    CreatedByUserId = team.CreatedByUserId,
                    MembersCount = team.Memberships.Count,
                    MyRole = team.Memberships.First(value => value.UserId == currentUserId).Role,
                    MyBadgeTitle = team.Memberships.First(value => value.UserId == currentUserId).BadgeTitle
                });
            }

            var membership = new TeamMembership
            {
                TeamId = team.Id,
                UserId = currentUserId,
                Role = TeamMemberRole.Member,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.TeamMemberships.AddAsync(membership);
            await _context.SaveChangesAsync();

            return Ok(new TeamDto
            {
                Id = team.Id,
                Name = team.Name,
                Description = team.Description,
                AvatarUrl = team.AvatarUrl,
                CoverImageUrl = team.CoverImageUrl,
                Visibility = team.Visibility,
                InviteCode = string.Empty,
                CreatedByUserId = team.CreatedByUserId,
                MembersCount = team.Memberships.Count + 1,
                MyRole = membership.Role,
                MyBadgeTitle = membership.BadgeTitle
            });
        }

        private async Task<ActionResult<TeamDto>> UploadTeamMedia(
            Guid id,
            Guid currentUserId,
            IFormFile file,
            bool isCover,
            CancellationToken cancellationToken)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var team = await _context.Teams
                .Include(value => value.Memberships)
                .FirstOrDefaultAsync(value => value.Id == id, cancellationToken);

            if (team == null)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            var actorMembership = team.Memberships.FirstOrDefault(value => value.UserId == currentUserId);
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Team media upload failed for team {TeamId}", id);
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Не удалось загрузить изображение команды." });
            }
        }

        private async Task<string> GenerateUniqueInviteCode()
        {
            while (true)
            {
                var code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
                var exists = await _context.Teams.AsNoTracking().AnyAsync(value => value.InviteCode == code);
                if (!exists)
                {
                    return code;
                }
            }
        }

        private static string NormalizeName(string value)
        {
            var parts = value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(" ", parts);
        }

        private static string? NormalizeDescription(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = NormalizeName(value);
            return normalized.Length > 1000 ? normalized[..1000] : normalized;
        }

        private static string? NormalizeBadgeTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = NormalizeName(value);
            return normalized.Length > 32 ? normalized[..32] : normalized;
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

        private async Task<bool> CanManageTeamAsync(Guid teamId, Guid userId)
        {
            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(value =>
                    value.TeamId == teamId &&
                    value.UserId == userId &&
                    (value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin));
        }

        private bool IsAllowedPwaLogoSource(Uri avatarUri)
        {
            if (avatarUri.Host.Equals("ik.imagekit.io", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var publicBaseUrl = _configuration["S3:PublicBaseUrl"];
            if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var storageUri))
            {
                return false;
            }

            var storagePath = storageUri.AbsolutePath.TrimEnd('/');
            return avatarUri.Scheme.Equals(storageUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   avatarUri.Host.Equals(storageUri.Host, StringComparison.OrdinalIgnoreCase) &&
                   avatarUri.Port == storageUri.Port &&
                   (string.IsNullOrEmpty(storagePath) ||
                    avatarUri.AbsolutePath.StartsWith($"{storagePath}/", StringComparison.Ordinal));
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
            int? membersCount = null)
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
                MyBadgeTitle = myBadgeTitle
            };
        }

        private static string? SerializeContacts(IEnumerable<TeamContactItemDto>? contacts)
        {
            var normalized = NormalizeContacts(contacts).ToList();
            return normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized);
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
