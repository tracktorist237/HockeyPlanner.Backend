using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Shared.Models.UniformColors;
using HockeyPlanner.Backend.WebAPI.Models.UniformColors;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/uniform-colors")]
    public class UniformColorsController : ControllerBase
    {
        private readonly IUniformColorService _uniformColorService;
        private readonly IFileStorageService _fileStorageService;
        private readonly ILogger<UniformColorsController> _logger;

        public UniformColorsController(
            IUniformColorService uniformColorService,
            IFileStorageService fileStorageService,
            ILogger<UniformColorsController> logger)
        {
            _uniformColorService = uniformColorService;
            _fileStorageService = fileStorageService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<UniformColorDto>>> GetAll([FromQuery] Guid teamId)
        {
            var items = await _uniformColorService.GetAll(teamId);
            return Ok(items);
        }

        [HttpPost]
        public async Task<ActionResult<UniformColorDto>> Create([FromBody] CreateUniformColorDto dto, [FromQuery] Guid currentUserId)
        {
            try
            {
                var item = await _uniformColorService.Create(dto, currentUserId);
                return Ok(item);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка создания цвета формы");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [HttpPut("{id:guid}")]
        public async Task<ActionResult<UniformColorDto>> Update(Guid id, [FromBody] UpdateUniformColorDto dto, [FromQuery] Guid currentUserId)
        {
            try
            {
                var item = await _uniformColorService.Update(id, dto, currentUserId);
                return Ok(item);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка редактирования цвета формы {UniformColorId}", id);
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete(Guid id, [FromQuery] Guid currentUserId)
        {
            try
            {
                await _uniformColorService.Delete(id, currentUserId);
                return NoContent();
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка удаления цвета формы {UniformColorId}", id);
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [HttpPost("upload")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(3 * 1024 * 1024)]
        public async Task<ActionResult<UniformColorDto>> UploadAndCreate(
            [FromForm] UploadUniformColorRequest request,
            [FromQuery] Guid currentUserId,
            CancellationToken cancellationToken)
        {
            try
            {
                var name = request.Name?.Trim();
                var file = request.File;

                if (string.IsNullOrWhiteSpace(name))
                    throw new BusinessRuleException("Название цвета формы обязательно");

                if (file == null || file.Length == 0)
                    throw new BusinessRuleException("Файл изображения не передан");

                if (file.Length > 3 * 1024 * 1024)
                    throw new BusinessRuleException("Размер файла не должен превышать 3 МБ");

                await _uniformColorService.EnsureCanCreate(currentUserId, request.TeamId);

                await using var stream = file.OpenReadStream();
                var uploadResult = await _fileStorageService.UploadAsync(
                    new FileStorageUploadRequest
                    {
                        Content = stream,
                        FileName = file.FileName,
                        ContentType = file.ContentType,
                        Folder = FileStorageFolders.Teams,
                        ScopeId = request.TeamId.ToString("N")
                    },
                    cancellationToken);

                var item = await _uniformColorService.Create(
                    new CreateUniformColorDto
                    {
                        Name = name,
                        ImageUrl = uploadResult.PublicUrl,
                        TeamId = request.TeamId
                    },
                    currentUserId);

                return Ok(item);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки цвета формы в файловое хранилище");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }
    }
}

