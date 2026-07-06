using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Extensions;
using HockeyPlanner.Backend.WebAPI.Models.Users;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UsersController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IFileStorageService _fileStorageService;
        private readonly ILogger<UsersController> _logger;

        public UsersController(
            AppDbContext context,
            IFileStorageService fileStorageService,
            ILogger<UsersController> logger)
        {
            _context = context;
            _fileStorageService = fileStorageService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<User>>> GetUsers()
        {
            return await _context.Users.AsNoTracking().ToListAsync();
        }

        [HttpGet("birthdays/today")]
        public async Task<ActionResult<BirthdaysTodayResponse>> GetBirthdaysToday([FromQuery] Guid? currentUserId)
        {
            var timeZoneId = Environment.GetEnvironmentVariable("BIRTHDAY_TIMEZONE") ?? "Europe/Moscow";
            TimeZoneInfo timeZone;
            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch
            {
                timeZone = TimeZoneInfo.Utc;
            }

            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
            var today = now.Date;
            var viewerUserId = currentUserId.GetValueOrDefault();
            if (viewerUserId == Guid.Empty)
            {
                viewerUserId = User.GetUserId() ?? Guid.Empty;
            }

            if (viewerUserId == Guid.Empty)
            {
                return Ok(new BirthdaysTodayResponse
                {
                    Date = today.ToString("yyyy-MM-dd"),
                    Users = new List<BirthdayUserDto>()
                });
            }

            var viewerTeamIds = await _context.TeamMemberships
                .AsNoTracking()
                .Where(membership => membership.UserId == viewerUserId)
                .Select(membership => membership.TeamId)
                .ToListAsync();

            if (viewerTeamIds.Count == 0)
            {
                return Ok(new BirthdaysTodayResponse
                {
                    Date = today.ToString("yyyy-MM-dd"),
                    Users = new List<BirthdayUserDto>()
                });
            }

            var users = await _context.Users
                .AsNoTracking()
                .Where(user => user.BirthDate.HasValue)
                .Where(user =>
                    user.Id != viewerUserId &&
                    _context.TeamMemberships.Any(membership =>
                        membership.UserId == user.Id &&
                        viewerTeamIds.Contains(membership.TeamId)))
                .ToListAsync();

            var birthdayUsers = users
                .Where(user =>
                {
                    var birthDateUtc = NormalizeToUtc(user.BirthDate!.Value);
                    var birthDateLocal = TimeZoneInfo.ConvertTimeFromUtc(birthDateUtc, timeZone);
                    return birthDateLocal.Month == today.Month && birthDateLocal.Day == today.Day;
                })
                .Select(user => new BirthdayUserDto
                {
                    UserId = user.Id,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    JerseyNumber = user.JerseyNumber,
                    Age = today.Year - TimeZoneInfo.ConvertTimeFromUtc(
                        NormalizeToUtc(user.BirthDate!.Value),
                        timeZone).Year
                })
                .OrderBy(user => user.LastName)
                .ThenBy(user => user.FirstName)
                .ToList();

            return Ok(new BirthdaysTodayResponse
            {
                Date = today.ToString("yyyy-MM-dd"),
                Users = birthdayUsers
            });
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<UserProfileDto>> GetUser(
            Guid id,
            [FromQuery] Guid? currentUserId,
            [FromQuery] Guid? teamId)
        {
            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == id);

            if (user == null)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var viewerUserId = currentUserId.GetValueOrDefault();
            if (viewerUserId == Guid.Empty)
            {
                viewerUserId = User.GetUserId() ?? Guid.Empty;
            }

            var settings = await GetOrCreatePrivacySettings(id);
            var viewerContext = await BuildViewerContext(id, viewerUserId, teamId);

            return ToProfileDto(user, settings, viewerContext);
        }

        [HttpGet("{id}/privacy-settings")]
        public async Task<ActionResult<UserPrivacySettingsDto>> GetPrivacySettings(Guid id, [FromQuery] Guid? currentUserId)
        {
            var actorUserId = currentUserId.GetValueOrDefault();
            if (actorUserId == Guid.Empty)
            {
                actorUserId = User.GetUserId() ?? Guid.Empty;
            }

            if (actorUserId != id)
            {
                return Forbid();
            }

            var userExists = await _context.Users.AsNoTracking().AnyAsync(value => value.Id == id);
            if (!userExists)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            return ToPrivacyDto(await GetOrCreatePrivacySettings(id));
        }

        [HttpPut("{id}/privacy-settings")]
        public async Task<ActionResult<UserPrivacySettingsDto>> UpdatePrivacySettings(
            Guid id,
            [FromQuery] Guid? currentUserId,
            [FromBody] UpdateUserPrivacySettingsRequest request)
        {
            var actorUserId = currentUserId.GetValueOrDefault();
            if (actorUserId == Guid.Empty)
            {
                actorUserId = User.GetUserId() ?? Guid.Empty;
            }

            if (actorUserId != id)
            {
                return Forbid();
            }

            if (!IsValidVisibility(request.EmailVisibility) ||
                !IsValidVisibility(request.PhoneVisibility) ||
                !IsValidVisibility(request.BirthDateVisibility) ||
                !IsValidVisibility(request.PhysicalVisibility) ||
                !IsValidVisibility(request.HockeyProfileVisibility) ||
                !IsValidVisibility(request.SpbhlProfileVisibility))
            {
                return BadRequest(new { message = "Некорректный уровень видимости." });
            }

            var userExists = await _context.Users.AsNoTracking().AnyAsync(value => value.Id == id);
            if (!userExists)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var settings = await GetOrCreatePrivacySettings(id);
            settings.EmailVisibility = request.EmailVisibility;
            settings.PhoneVisibility = request.PhoneVisibility;
            settings.BirthDateVisibility = request.BirthDateVisibility;
            settings.PhysicalVisibility = request.PhysicalVisibility;
            settings.HockeyProfileVisibility = request.HockeyProfileVisibility;
            settings.SpbhlProfileVisibility = request.SpbhlProfileVisibility;
            settings.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return ToPrivacyDto(settings);
        }

        [HttpPut("{id}")]
        public async Task<ActionResult<User>> PutUser(Guid id, [FromBody] UpdateUserRequest request)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
            {
                return BadRequest(new { message = "Имя и фамилия обязательны." });
            }

            var normalizedFirstName = NormalizeName(request.FirstName);
            var normalizedLastName = NormalizeName(request.LastName);

            user.FirstName = normalizedFirstName;
            user.LastName = normalizedLastName;
            user.JerseyNumber = request.JerseyNumber;
            user.PrimaryPosition = request.PrimaryPosition.HasValue ? (Position?)request.PrimaryPosition.Value : null;
            user.Handedness = request.Handedness.HasValue ? (Handedness?)request.Handedness.Value : null;
            user.Height = request.Height;
            user.Weight = request.Weight;
            user.BirthDate = request.BirthDate?.ToUniversalTime();
            user.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
            user.PhotoUrl = string.IsNullOrWhiteSpace(request.PhotoUrl) ? null : request.PhotoUrl.Trim();
            user.SpbhlPlayerId = request.SpbhlPlayerId;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok(user);
        }

        [HttpPost]
        public async Task<ActionResult<User>> PostUser(User user)
        {
            if (string.IsNullOrWhiteSpace(user.FirstName) || string.IsNullOrWhiteSpace(user.LastName))
            {
                return BadRequest(new { message = "Имя и фамилия обязательны." });
            }

            var normalizedFirstName = NormalizeName(user.FirstName);
            var normalizedLastName = NormalizeName(user.LastName);
            var normalizedFirstNameLower = normalizedFirstName.ToLowerInvariant();
            var normalizedLastNameLower = normalizedLastName.ToLowerInvariant();

            var duplicateExists = await _context.Users
                .AsNoTracking()
                .AnyAsync(u =>
                    u.FirstName != null &&
                    u.LastName != null &&
                    u.FirstName.Trim().ToLower() == normalizedFirstNameLower &&
                    u.LastName.Trim().ToLower() == normalizedLastNameLower);

            if (duplicateExists)
            {
                return Conflict(new { message = "Пользователь с таким именем и фамилией уже существует." });
            }

            user.FirstName = normalizedFirstName;
            user.LastName = normalizedLastName;

            if (user.BirthDate != null)
            {
                user.BirthDate = user.BirthDate.Value.ToUniversalTime();
            }

            await _context.Users.AddAsync(user);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetUser), new { id = user.Id }, user);
        }

        [HttpPost("{id}/avatar/upload")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<User>> UploadAvatar(
            Guid id,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            if (file == null || file.Length == 0)
            {
                return BadRequest(new { message = "Файл не передан." });
            }

            if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "Нужен файл изображения." });
            }

            if (file.Length > 5 * 1024 * 1024)
            {
                return BadRequest(new { message = "Размер файла не должен превышать 5 МБ." });
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var allowedExtensions = new HashSet<string> { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
            if (!allowedExtensions.Contains(extension))
            {
                return BadRequest(new { message = "Поддерживаются форматы JPG, PNG, WEBP, GIF." });
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
                        Folder = FileStorageFolders.Avatars,
                        ScopeId = id.ToString("N")
                    },
                    cancellationToken);

                user.PhotoUrl = uploadResult.PublicUrl;
                user.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                return Ok(user);
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected avatar upload error for user {UserId}", id);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Не удалось загрузить аватарку во внешний сервис. Попробуйте ещё раз."
                });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteUser(Guid id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        private async Task<UserPrivacySettings> GetOrCreatePrivacySettings(Guid userId)
        {
            var settings = await _context.UserPrivacySettings.FirstOrDefaultAsync(value => value.UserId == userId);
            if (settings != null)
            {
                return settings;
            }

            settings = new UserPrivacySettings
            {
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.UserPrivacySettings.AddAsync(settings);
            await _context.SaveChangesAsync();
            return settings;
        }

        private async Task<UserPrivacyViewerContext> BuildViewerContext(Guid targetUserId, Guid viewerUserId, Guid? teamId)
        {
            if (viewerUserId == Guid.Empty)
            {
                return new UserPrivacyViewerContext();
            }

            var viewerAppRole = await _context.Users
                .AsNoTracking()
                .Where(value => value.Id == viewerUserId)
                .Select(value => value.AppRole)
                .FirstOrDefaultAsync();

            if (viewerAppRole == AppRole.SuperAdmin || viewerUserId == targetUserId)
            {
                return new UserPrivacyViewerContext
                {
                    IsOwner = viewerUserId == targetUserId,
                    IsSuperAdmin = viewerAppRole == AppRole.SuperAdmin,
                    IsTeammate = true,
                    IsTeamAdmin = true
                };
            }

            var targetTeamIdsQuery = _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.UserId == targetUserId);

            if (teamId.HasValue)
            {
                targetTeamIdsQuery = targetTeamIdsQuery.Where(value => value.TeamId == teamId.Value);
            }

            var targetTeamIds = await targetTeamIdsQuery
                .Select(value => value.TeamId)
                .ToListAsync();

            if (targetTeamIds.Count == 0)
            {
                return new UserPrivacyViewerContext();
            }

            var viewerMemberships = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.UserId == viewerUserId && targetTeamIds.Contains(value.TeamId))
                .Select(value => new { value.Role })
                .ToListAsync();

            return new UserPrivacyViewerContext
            {
                IsTeammate = viewerMemberships.Count > 0,
                IsTeamAdmin = viewerMemberships.Any(value => value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin)
            };
        }

        private static UserProfileDto ToProfileDto(User user, UserPrivacySettings settings, UserPrivacyViewerContext viewerContext)
        {
            var canSeeEmail = CanSee(settings.EmailVisibility, viewerContext);
            var canSeePhone = CanSee(settings.PhoneVisibility, viewerContext);
            var canSeeBirthDate = CanSee(settings.BirthDateVisibility, viewerContext);
            var canSeePhysical = CanSee(settings.PhysicalVisibility, viewerContext);
            var canSeeHockeyProfile = CanSee(settings.HockeyProfileVisibility, viewerContext);
            var canSeeSpbhlProfile = CanSee(settings.SpbhlProfileVisibility, viewerContext);

            return new UserProfileDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = canSeeEmail ? user.Email : null,
                EmailConfirmed = viewerContext.IsOwner || viewerContext.IsSuperAdmin ? user.EmailConfirmed : false,
                Phone = canSeePhone ? user.Phone : null,
                PhotoUrl = user.PhotoUrl,
                SpbhlPlayerId = canSeeSpbhlProfile ? user.SpbhlPlayerId : null,
                Role = user.Role,
                AppRole = viewerContext.IsSuperAdmin ? user.AppRole : AppRole.User,
                JerseyNumber = user.JerseyNumber,
                PrimaryPosition = canSeeHockeyProfile ? user.PrimaryPosition : null,
                Handedness = canSeeHockeyProfile ? user.Handedness : null,
                Height = canSeePhysical ? user.Height : null,
                Weight = canSeePhysical ? user.Weight : null,
                BirthDate = canSeeBirthDate ? user.BirthDate : null,
                CreatedAt = user.CreatedAt,
                UpdatedAt = user.UpdatedAt,
                FullName = user.FullName
            };
        }

        private static bool CanSee(UserDataVisibility visibility, UserPrivacyViewerContext viewerContext)
        {
            if (viewerContext.IsOwner || viewerContext.IsSuperAdmin)
            {
                return true;
            }

            return visibility switch
            {
                UserDataVisibility.Everyone => true,
                UserDataVisibility.Teammates => viewerContext.IsTeammate,
                UserDataVisibility.TeamAdmins => viewerContext.IsTeamAdmin,
                _ => false
            };
        }

        private static UserPrivacySettingsDto ToPrivacyDto(UserPrivacySettings settings) =>
            new()
            {
                UserId = settings.UserId,
                EmailVisibility = settings.EmailVisibility,
                PhoneVisibility = settings.PhoneVisibility,
                BirthDateVisibility = settings.BirthDateVisibility,
                PhysicalVisibility = settings.PhysicalVisibility,
                HockeyProfileVisibility = settings.HockeyProfileVisibility,
                SpbhlProfileVisibility = settings.SpbhlProfileVisibility
            };

        private static bool IsValidVisibility(UserDataVisibility visibility) =>
            Enum.IsDefined(typeof(UserDataVisibility), visibility);

        private sealed class UserPrivacyViewerContext
        {
            public bool IsOwner { get; set; }
            public bool IsSuperAdmin { get; set; }
            public bool IsTeammate { get; set; }
            public bool IsTeamAdmin { get; set; }
        }

        private static DateTime NormalizeToUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
        }

        private static string NormalizeName(string value)
        {
            var parts = value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(" ", parts);
        }
    }
}
