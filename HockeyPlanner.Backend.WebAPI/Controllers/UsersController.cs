using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Users;
using HockeyPlanner.Backend.WebAPI.Services;
using HockeyPlanner.Backend.Shared.Models.Users;
using Microsoft.AspNetCore.Authorization;
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
        private readonly IUserService _userService;
        private readonly ICurrentUser _currentUser;

        public UsersController(
            AppDbContext context,
            IFileStorageService fileStorageService,
            ILogger<UsersController> logger,
            IUserService userService,
            ICurrentUser currentUser)
        {
            _context = context;
            _fileStorageService = fileStorageService;
            _logger = logger;
            _userService = userService;
            _currentUser = currentUser;
        }

        [HttpGet]
        [Authorize]
        public async Task<ActionResult<IReadOnlyList<UserSummaryDto>>> GetUsers(
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
            {
                return Unauthorized();
            }

            return Ok(await _userService.GetDirectory(
                _currentUser.UserId.Value,
                cancellationToken));
        }

        [HttpGet("birthdays/today")]
        [Authorize]
        public async Task<ActionResult<BirthdaysTodayResponse>> GetBirthdaysToday(
            [FromQuery] Guid? currentUserId,
            CancellationToken cancellationToken)
        {
            _ = currentUserId;
            var viewerUserId = _currentUser.UserId;
            if (!viewerUserId.HasValue)
            {
                return Unauthorized();
            }

            return Ok(await _userService.GetBirthdaysToday(
                viewerUserId.Value,
                cancellationToken));
        }

        [HttpGet("{id}")]
        [AllowAnonymous]
        public async Task<ActionResult<UserProfileDto>> GetUser(
            Guid id,
            [FromQuery] Guid? currentUserId,
            [FromQuery] Guid? teamId,
            CancellationToken cancellationToken)
        {
            _ = currentUserId;
            if (_currentUser.IsAuthenticated && !_currentUser.UserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return await _userService.GetProfile(
                    id,
                    _currentUser.UserId,
                    teamId,
                    cancellationToken);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpGet("{id}/privacy-settings")]
        [Authorize]
        public async Task<ActionResult<UserPrivacySettingsDto>> GetPrivacySettings(
            Guid id,
            [FromQuery] Guid? currentUserId,
            CancellationToken cancellationToken)
        {
            _ = currentUserId;
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return await _userService.GetPrivacySettings(
                    id,
                    actorUserId.Value,
                    cancellationToken);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (UnauthorizedException)
            {
                return Forbid();
            }
        }

        [HttpPut("{id}/privacy-settings")]
        [Authorize]
        public async Task<ActionResult<UserPrivacySettingsDto>> UpdatePrivacySettings(
            Guid id,
            [FromQuery] Guid? currentUserId,
            [FromBody] UpdateUserPrivacySettingsRequest request,
            CancellationToken cancellationToken)
        {
            _ = currentUserId;
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return await _userService.UpdatePrivacySettings(
                    id,
                    actorUserId.Value,
                    request,
                    cancellationToken);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (UnauthorizedException)
            {
                return Forbid();
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
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

        private static string NormalizeName(string value)
        {
            var parts = value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(" ", parts);
        }
    }
}
