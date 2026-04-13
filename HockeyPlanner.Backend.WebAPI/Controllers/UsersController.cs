using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
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
        private readonly IImageKitUploader _imageKitUploader;
        private readonly ILogger<UsersController> _logger;

        public UsersController(
            AppDbContext context,
            IImageKitUploader imageKitUploader,
            ILogger<UsersController> logger)
        {
            _context = context;
            _imageKitUploader = imageKitUploader;
            _logger = logger;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<User>>> GetUsers()
        {
            return await _context.Users.AsNoTracking().ToListAsync();
        }

        [HttpGet("birthdays/today")]
        public async Task<ActionResult<BirthdaysTodayResponse>> GetBirthdaysToday()
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

            var users = await _context.Users
                .AsNoTracking()
                .Where(user => user.BirthDate.HasValue)
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
        public async Task<ActionResult<User>> GetUser(Guid id)
        {
            var user = await _context.Users.FindAsync(id);

            if (user == null)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            return user;
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

            user.FirstName = request.FirstName.Trim();
            user.LastName = request.LastName.Trim();
            user.JerseyNumber = request.JerseyNumber;
            user.PrimaryPosition = request.PrimaryPosition.HasValue ? (Position?)request.PrimaryPosition.Value : null;
            user.Handedness = request.Handedness.HasValue ? (Handedness?)request.Handedness.Value : null;
            user.Height = request.Height;
            user.Weight = request.Weight;
            user.BirthDate = request.BirthDate?.ToUniversalTime();
            user.PhotoUrl = string.IsNullOrWhiteSpace(request.PhotoUrl) ? null : request.PhotoUrl.Trim();
            user.SpbhlPlayerId = request.SpbhlPlayerId;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok(user);
        }

        [HttpPost]
        public async Task<ActionResult<User>> PostUser(User user)
        {
            var existingUser = await _context.Users
                .FirstOrDefaultAsync(u => u.FirstName == user.FirstName
                    && u.LastName == user.LastName);

            if (existingUser != null)
            {
                return Conflict(new { message = "Пользователь с таким именем и фамилией уже существует." });
            }

            if (user.BirthDate != null)
                user.BirthDate = user.BirthDate.Value.ToUniversalTime();

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
                var uploadedUrl = await _imageKitUploader.UploadAsync(
                    stream,
                    file.FileName,
                    "/avatars",
                    cancellationToken);

                user.PhotoUrl = uploadedUrl;
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

        private static DateTime NormalizeToUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
        }
    }
}
