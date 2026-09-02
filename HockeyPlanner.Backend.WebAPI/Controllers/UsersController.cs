using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.WebAPI.Services;
using HockeyPlanner.Backend.Shared.Models.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UsersController : ControllerBase
    {
        private readonly IFileStorageService _fileStorageService;
        private readonly ILogger<UsersController> _logger;
        private readonly IUserService _userService;
        private readonly ICurrentUser _currentUser;

        public UsersController(
            IFileStorageService fileStorageService,
            ILogger<UsersController> logger,
            IUserService userService,
            ICurrentUser currentUser)
        {
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
        [Authorize]
        public async Task<ActionResult<UserProfileDto>> PutUser(
            Guid id,
            [FromBody] UpdateUserRequest request,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return Ok(await _userService.UpdateUser(
                    id,
                    actorUserId.Value,
                    request,
                    cancellationToken));
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

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> PostUser(CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                await _userService.RejectLegacyUserCreation(cancellationToken);
                return Forbid();
            }
            catch (UnauthorizedException)
            {
                return Forbid();
            }
        }

        [HttpPost("{id}/avatar/upload")]
        [Authorize]
        [DisableFormValueModelBinding]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<UserProfileDto>> UploadAvatar(
            Guid id,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                await _userService.EnsureAvatarUploadAllowed(
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

            if (!Request.HasFormContentType)
            {
                return BadRequest(new { message = "Ожидаются данные формы multipart/form-data." });
            }

            IFormCollection form;
            try
            {
                form = await Request.ReadFormAsync(cancellationToken);
            }
            catch (InvalidDataException)
            {
                return BadRequest(new { message = "Некорректные данные формы." });
            }

            var file = form.Files.GetFile("file");

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

                return Ok(await _userService.UpdateAvatar(
                    id,
                    actorUserId.Value,
                    uploadResult.PublicUrl,
                    cancellationToken));
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
        [Authorize]
        public async Task<IActionResult> DeleteUser(Guid id, CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                await _userService.RejectUserDeletion(id, cancellationToken);
                return Forbid();
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

        private sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
        {
            public void OnResourceExecuting(ResourceExecutingContext context)
            {
                context.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
                context.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
                context.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
            }

            public void OnResourceExecuted(ResourceExecutedContext context)
            {
            }
        }
    }

}
