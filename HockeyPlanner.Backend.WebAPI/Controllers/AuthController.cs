using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Extensions;
using HockeyPlanner.Backend.WebAPI.Models.Auth;
using HockeyPlanner.Backend.WebAPI.Options;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IAuthTokenService _tokenService;
        private readonly IAuthEmailSender _emailSender;
        private readonly PasswordHasher<User> _passwordHasher;
        private readonly JwtOptions _jwtOptions;

        public AuthController(
            AppDbContext context,
            IAuthTokenService tokenService,
            IAuthEmailSender emailSender,
            IOptions<JwtOptions> jwtOptions)
        {
            _context = context;
            _tokenService = tokenService;
            _emailSender = emailSender;
            _jwtOptions = jwtOptions.Value;
            _passwordHasher = new PasswordHasher<User>();
        }

        [AllowAnonymous]
        [HttpPost("register")]
        public async Task<ActionResult<AuthResponse>> Register(
            [FromBody] RegisterRequest request,
            CancellationToken cancellationToken)
        {
            var email = NormalizeEmail(request.Email);
            if (string.IsNullOrWhiteSpace(request.FirstName) ||
                string.IsNullOrWhiteSpace(request.LastName) ||
                string.IsNullOrWhiteSpace(email) ||
                string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(new { message = "Имя, фамилия, email и пароль обязательны." });
            }

            if (request.Password.Length < 8)
            {
                return BadRequest(new { message = "Пароль должен быть не короче 8 символов." });
            }

            var emailExists = await _context.Users
                .AsNoTracking()
                .AnyAsync(user => user.Email != null && user.Email.ToLower() == email, cancellationToken);

            if (emailExists)
            {
                return Conflict(new { message = "Эта почта уже используется." });
            }

            var user = new User
            {
                FirstName = NormalizeName(request.FirstName),
                LastName = NormalizeName(request.LastName),
                Email = email,
                EmailConfirmed = false,
                JerseyNumber = request.JerseyNumber,
                Role = UserRole.Player,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);
            user.PasswordUpdatedAt = DateTime.UtcNow;

            await _context.Users.AddAsync(user, cancellationToken);

            var emailToken = CreateEmailConfirmationToken(user);
            await _context.EmailConfirmationTokens.AddAsync(emailToken.entity, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await _emailSender.SendEmailConfirmation(user, emailToken.rawToken, cancellationToken);

            return Ok(await CreateAuthResponse(user, cancellationToken));
        }

        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<ActionResult<AuthResponse>> Login(
            [FromBody] LoginRequest request,
            CancellationToken cancellationToken)
        {
            var email = NormalizeEmail(request.Email);
            var user = await _context.Users
                .FirstOrDefaultAsync(value => value.Email != null && value.Email.ToLower() == email, cancellationToken);

            if (user == null || string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                return Unauthorized(new { message = "Неверный email или пароль." });
            }

            var passwordResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
            if (passwordResult == PasswordVerificationResult.Failed)
            {
                return Unauthorized(new { message = "Неверный email или пароль." });
            }

            return Ok(await CreateAuthResponse(user, cancellationToken));
        }

        [Authorize]
        [HttpPost("link-player")]
        public async Task<ActionResult<AuthResponse>> LinkPlayer(
            [FromBody] LinkPlayerRequest request,
            CancellationToken cancellationToken)
        {
            var authUserId = User.GetUserId();
            if (!authUserId.HasValue)
            {
                return Unauthorized(new { message = "Пользователь не авторизован." });
            }

            if (request.UserId == Guid.Empty)
            {
                return BadRequest(new { message = "Нужно выбрать профиль игрока." });
            }

            if (authUserId.Value == request.UserId)
            {
                var sameUser = await _context.Users
                    .FirstOrDefaultAsync(value => value.Id == authUserId.Value, cancellationToken);
                return sameUser == null
                    ? Unauthorized(new { message = "Пользователь не найден." })
                    : Ok(await CreateAuthResponse(sameUser, cancellationToken));
            }

            var authUser = await _context.Users
                .FirstOrDefaultAsync(value => value.Id == authUserId.Value, cancellationToken);
            var playerUser = await _context.Users
                .FirstOrDefaultAsync(value => value.Id == request.UserId, cancellationToken);

            if (authUser == null)
            {
                return Unauthorized(new { message = "Пользователь не найден." });
            }

            if (playerUser == null)
            {
                return NotFound(new { message = "Профиль игрока не найден." });
            }

            if (string.IsNullOrWhiteSpace(authUser.Email) || string.IsNullOrWhiteSpace(authUser.PasswordHash))
            {
                return Conflict(new { message = "У текущего аккаунта нет email/пароля для привязки." });
            }

            if (!string.IsNullOrWhiteSpace(playerUser.Email) || !string.IsNullOrWhiteSpace(playerUser.PasswordHash))
            {
                return Conflict(new { message = "Этот профиль игрока уже привязан к аккаунту." });
            }

            var authUserHasDomainLinks = await HasDomainLinks(authUser.Id, cancellationToken);
            if (authUserHasDomainLinks)
            {
                return Conflict(new
                {
                    message = "У этого аккаунта уже есть хоккейные данные, автоматическая привязка невозможна."
                });
            }

            var email = authUser.Email;
            var emailConfirmed = authUser.EmailConfirmed;
            var passwordHash = authUser.PasswordHash;
            var passwordUpdatedAt = authUser.PasswordUpdatedAt;

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            authUser.Email = null;
            authUser.EmailConfirmed = false;
            authUser.PasswordHash = null;
            authUser.PasswordUpdatedAt = null;
            authUser.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            playerUser.Email = email;
            playerUser.EmailConfirmed = emailConfirmed;
            playerUser.PasswordHash = passwordHash;
            playerUser.PasswordUpdatedAt = passwordUpdatedAt;
            playerUser.UpdatedAt = DateTime.UtcNow;
            (EmailConfirmationToken entity, string rawToken)? confirmationToken = null;
            if (!playerUser.EmailConfirmed)
            {
                confirmationToken = CreateEmailConfirmationToken(playerUser);
                await _context.EmailConfirmationTokens.AddAsync(confirmationToken.Value.entity, cancellationToken);
            }

            var oldRefreshTokens = await _context.RefreshTokens
                .Where(value => value.UserId == authUser.Id && value.RevokedAt == null)
                .ToListAsync(cancellationToken);
            foreach (var token in oldRefreshTokens)
            {
                token.RevokedAt = DateTime.UtcNow;
            }

            _context.Users.Remove(authUser);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (confirmationToken.HasValue)
            {
                await _emailSender.SendEmailConfirmation(playerUser, confirmationToken.Value.rawToken, cancellationToken);
            }

            return Ok(await CreateAuthResponse(playerUser, cancellationToken));
        }

        [Authorize]
        [HttpPost("change-email")]
        public async Task<ActionResult<AuthResponse>> ChangeEmail(
            [FromBody] ChangeEmailRequest request,
            CancellationToken cancellationToken)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(new { message = "Пользователь не авторизован." });
            }

            var newEmail = NormalizeEmail(request.NewEmail);
            if (string.IsNullOrWhiteSpace(newEmail) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(new { message = "Новый email и пароль обязательны." });
            }

            var user = await _context.Users
                .FirstOrDefaultAsync(value => value.Id == userId.Value, cancellationToken);

            if (user == null)
            {
                return Unauthorized(new { message = "Пользователь не найден." });
            }

            if (string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                return Conflict(new { message = "У этого профиля нет аккаунта с паролем." });
            }

            var passwordResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
            if (passwordResult == PasswordVerificationResult.Failed)
            {
                return Unauthorized(new { message = "Пароль введен неверно." });
            }

            var emailExists = await _context.Users
                .AsNoTracking()
                .AnyAsync(
                    value => value.Id != user.Id && value.Email != null && value.Email.ToLower() == newEmail,
                    cancellationToken);

            if (emailExists)
            {
                return Conflict(new { message = "Эта почта уже используется." });
            }

            if (string.Equals(user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            {
                return Ok(await CreateAuthResponse(user, cancellationToken));
            }

            var activeEmailTokens = await _context.EmailConfirmationTokens
                .Where(value => value.UserId == user.Id && value.UsedAt == null)
                .ToListAsync(cancellationToken);
            foreach (var token in activeEmailTokens)
            {
                token.ExpiresAt = DateTime.UtcNow;
                token.UpdatedAt = DateTime.UtcNow;
            }

            user.Email = newEmail;
            user.EmailConfirmed = false;
            user.UpdatedAt = DateTime.UtcNow;

            var emailToken = CreateEmailConfirmationToken(user);
            await _context.EmailConfirmationTokens.AddAsync(emailToken.entity, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await _emailSender.SendEmailConfirmation(user, emailToken.rawToken, cancellationToken);

            return Ok(await CreateAuthResponse(user, cancellationToken));
        }

        [Authorize]
        [HttpPost("resend-email-confirmation")]
        public async Task<IActionResult> ResendEmailConfirmation(CancellationToken cancellationToken)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(new { message = "Пользователь не авторизован." });
            }

            var user = await _context.Users
                .FirstOrDefaultAsync(value => value.Id == userId.Value, cancellationToken);

            if (user == null)
            {
                return Unauthorized(new { message = "Пользователь не найден." });
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return BadRequest(new { message = "У профиля не указана почта." });
            }

            if (user.EmailConfirmed)
            {
                return Ok(new { message = "Почта уже подтверждена." });
            }

            var activeEmailTokens = await _context.EmailConfirmationTokens
                .Where(value => value.UserId == user.Id && value.UsedAt == null)
                .ToListAsync(cancellationToken);
            foreach (var token in activeEmailTokens)
            {
                token.ExpiresAt = DateTime.UtcNow;
                token.UpdatedAt = DateTime.UtcNow;
            }

            var emailToken = CreateEmailConfirmationToken(user);
            await _context.EmailConfirmationTokens.AddAsync(emailToken.entity, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await _emailSender.SendEmailConfirmation(user, emailToken.rawToken, cancellationToken);

            return Ok(new { message = "Письмо подтверждения отправлено." });
        }

        [Authorize]
        [HttpPost("change-password")]
        public async Task<ActionResult<AuthResponse>> ChangePassword(
            [FromBody] ChangePasswordRequest request,
            CancellationToken cancellationToken)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(new { message = "Пользователь не авторизован." });
            }

            if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return BadRequest(new { message = "Текущий и новый пароль обязательны." });
            }

            if (request.NewPassword.Length < 8)
            {
                return BadRequest(new { message = "Пароль должен быть не короче 8 символов." });
            }

            var user = await _context.Users
                .FirstOrDefaultAsync(value => value.Id == userId.Value, cancellationToken);

            if (user == null)
            {
                return Unauthorized(new { message = "Пользователь не найден." });
            }

            if (string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                return Conflict(new { message = "У этого профиля нет аккаунта с паролем." });
            }

            var passwordResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword);
            if (passwordResult == PasswordVerificationResult.Failed)
            {
                return Unauthorized(new { message = "Пароль введен неверно." });
            }

            user.PasswordHash = _passwordHasher.HashPassword(user, request.NewPassword);
            user.PasswordUpdatedAt = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;

            var activeTokens = await _context.RefreshTokens
                .Where(value => value.UserId == user.Id && value.RevokedAt == null)
                .ToListAsync(cancellationToken);
            foreach (var token in activeTokens)
            {
                token.RevokedAt = DateTime.UtcNow;
                token.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(await CreateAuthResponse(user, cancellationToken));
        }

        [AllowAnonymous]
        [HttpPost("refresh")]
        public async Task<ActionResult<AuthResponse>> Refresh(
            [FromBody] RefreshTokenRequest request,
            CancellationToken cancellationToken)
        {
            var tokenHash = _tokenService.HashToken(request.RefreshToken);
            var token = await _context.RefreshTokens
                .Include(value => value.User)
                .FirstOrDefaultAsync(value => value.TokenHash == tokenHash, cancellationToken);

            if (token == null || !token.IsActive)
            {
                return Unauthorized(new { message = "Сессия истекла. Войдите заново." });
            }

            token.UsedAt = DateTime.UtcNow;
            token.RevokedAt = DateTime.UtcNow;

            var response = await CreateAuthResponse(token.User, cancellationToken);
            var replacementHash = _tokenService.HashToken(response.RefreshToken);
            var replacement = await _context.RefreshTokens
                .AsNoTracking()
                .FirstAsync(value => value.TokenHash == replacementHash, cancellationToken);
            token.ReplacedByTokenId = replacement.Id;

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(response);
        }

        [Authorize]
        [HttpGet("me")]
        public async Task<ActionResult<AuthUserResponse>> Me(CancellationToken cancellationToken)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(new { message = "Пользователь не авторизован." });
            }

            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == userId.Value, cancellationToken);

            if (user == null)
            {
                return Unauthorized(new { message = "Пользователь не найден." });
            }

            return Ok(MapUser(user));
        }

        [Authorize]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                var tokenHash = _tokenService.HashToken(request.RefreshToken);
                var token = await _context.RefreshTokens
                    .FirstOrDefaultAsync(value => value.TokenHash == tokenHash, cancellationToken);

                if (token != null)
                {
                    token.RevokedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }

            return Ok(new { message = "Вы вышли из аккаунта." });
        }

        [AllowAnonymous]
        [HttpPost("confirm-email")]
        public async Task<IActionResult> ConfirmEmail(
            [FromBody] ConfirmEmailRequest request,
            CancellationToken cancellationToken)
        {
            var tokenHash = _tokenService.HashToken(request.Token);
            var token = await _context.EmailConfirmationTokens
                .Include(value => value.User)
                .FirstOrDefaultAsync(value => value.TokenHash == tokenHash, cancellationToken);

            if (token == null || token.ExpiresAt <= DateTime.UtcNow)
            {
                return BadRequest(new { message = "Ссылка подтверждения недействительна или устарела." });
            }

            if (token.UsedAt != null && !token.User.EmailConfirmed)
            {
                return BadRequest(new { message = "Ссылка подтверждения уже недействительна. Отправьте письмо ещё раз." });
            }

            if (token.User.EmailConfirmed)
            {
                return Ok(new { message = "Почта уже подтверждена." });
            }

            token.UsedAt = DateTime.UtcNow;
            token.User.EmailConfirmed = true;
            token.User.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(new { message = "Почта подтверждена." });
        }

        [AllowAnonymous]
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword(
            [FromBody] ForgotPasswordRequest request,
            CancellationToken cancellationToken)
        {
            var email = NormalizeEmail(request.Email);
            var user = await _context.Users
                .FirstOrDefaultAsync(value => value.Email != null && value.Email.ToLower() == email, cancellationToken);

            if (user != null)
            {
                var resetToken = CreatePasswordResetToken(user);
                await _context.PasswordResetTokens.AddAsync(resetToken.entity, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
                await _emailSender.SendPasswordReset(user, resetToken.rawToken, cancellationToken);
            }

            return Ok(new { message = "Если такая почта есть в системе, мы отправили письмо для смены пароля." });
        }

        [AllowAnonymous]
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword(
            [FromBody] ResetPasswordRequest request,
            CancellationToken cancellationToken)
        {
            if (request.NewPassword.Length < 8)
            {
                return BadRequest(new { message = "Пароль должен быть не короче 8 символов." });
            }

            var tokenHash = _tokenService.HashToken(request.Token);
            var token = await _context.PasswordResetTokens
                .Include(value => value.User)
                .FirstOrDefaultAsync(value => value.TokenHash == tokenHash, cancellationToken);

            if (token == null || token.UsedAt != null || token.ExpiresAt <= DateTime.UtcNow)
            {
                return BadRequest(new { message = "Ссылка для смены пароля недействительна или устарела." });
            }

            token.UsedAt = DateTime.UtcNow;
            token.User.PasswordHash = _passwordHasher.HashPassword(token.User, request.NewPassword);
            token.User.PasswordUpdatedAt = DateTime.UtcNow;
            token.User.UpdatedAt = DateTime.UtcNow;

            var activeTokens = await _context.RefreshTokens
                .Where(value => value.UserId == token.UserId && value.RevokedAt == null)
                .ToListAsync(cancellationToken);
            foreach (var refreshToken in activeTokens)
            {
                refreshToken.RevokedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(new { message = "Пароль обновлен." });
        }

        private async Task<AuthResponse> CreateAuthResponse(User user, CancellationToken cancellationToken)
        {
            var refreshToken = _tokenService.CreateOpaqueToken();
            var refreshTokenEntity = new RefreshToken
            {
                UserId = user.Id,
                TokenHash = _tokenService.HashToken(refreshToken),
                UserAgent = Request.Headers.UserAgent.ToString(),
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                ExpiresAt = DateTime.UtcNow.AddDays(_jwtOptions.RefreshTokenDays),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.RefreshTokens.AddAsync(refreshTokenEntity, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            return new AuthResponse
            {
                AccessToken = _tokenService.CreateAccessToken(user),
                RefreshToken = refreshToken,
                AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes),
                User = MapUser(user)
            };
        }

        private (EmailConfirmationToken entity, string rawToken) CreateEmailConfirmationToken(User user)
        {
            var token = _tokenService.CreateOpaqueToken();
            return (new EmailConfirmationToken
            {
                UserId = user.Id,
                TokenHash = _tokenService.HashToken(token),
                ExpiresAt = DateTime.UtcNow.AddHours(_jwtOptions.EmailTokenHours),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }, token);
        }

        private (PasswordResetToken entity, string rawToken) CreatePasswordResetToken(User user)
        {
            var token = _tokenService.CreateOpaqueToken();
            return (new PasswordResetToken
            {
                UserId = user.Id,
                TokenHash = _tokenService.HashToken(token),
                ExpiresAt = DateTime.UtcNow.AddMinutes(_jwtOptions.PasswordResetTokenMinutes),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }, token);
        }

        private async Task<bool> HasDomainLinks(Guid userId, CancellationToken cancellationToken)
        {
            return await _context.Attendances.AnyAsync(value => value.UserId == userId, cancellationToken) ||
                   await _context.Players.AnyAsync(value => value.UserId == userId, cancellationToken) ||
                   await _context.TeamMemberships.AnyAsync(value => value.UserId == userId, cancellationToken) ||
                   await _context.Teams.AnyAsync(value => value.CreatedByUserId == userId, cancellationToken) ||
                   await _context.Exercises.AnyAsync(value => value.CreatedByUserId == userId, cancellationToken) ||
                   await _context.UniformColors.AnyAsync(value => value.CreatedByUserId == userId, cancellationToken);
        }

        private static AuthUserResponse MapUser(User user)
        {
            return new AuthUserResponse
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                EmailConfirmed = user.EmailConfirmed,
                JerseyNumber = user.JerseyNumber,
                Role = user.Role,
                PhotoUrl = user.PhotoUrl,
                SpbhlPlayerId = user.SpbhlPlayerId,
                FullName = user.FullName
            };
        }

        private static string NormalizeEmail(string? value)
        {
            return value?.Trim().ToLowerInvariant() ?? string.Empty;
        }

        private static string NormalizeName(string value)
        {
            var parts = value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(" ", parts);
        }
    }
}
