using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Shared.Models.UniformColors;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/uniform-colors")]
    public class UniformColorsController : ControllerBase
    {
        private readonly IUniformColorService _uniformColorService;
        private readonly IImageKitUploader _imageKitUploader;
        private readonly ILogger<UniformColorsController> _logger;

        public UniformColorsController(
            IUniformColorService uniformColorService,
            IImageKitUploader imageKitUploader,
            ILogger<UniformColorsController> logger)
        {
            _uniformColorService = uniformColorService;
            _imageKitUploader = imageKitUploader;
            _logger = logger;
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<UniformColorDto>>> GetAll()
        {
            var items = await _uniformColorService.GetAll();
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

        [HttpPost("upload")]
        [RequestSizeLimit(3 * 1024 * 1024)]
        public async Task<ActionResult<UniformColorDto>> UploadAndCreate(
            [FromForm] string name,
            [FromForm] IFormFile file,
            [FromQuery] Guid currentUserId,
            CancellationToken cancellationToken)
        {
            try
            {
                if (file == null || file.Length == 0)
                    throw new BusinessRuleException("Файл изображения не передан");

                if (file.Length > 3 * 1024 * 1024)
                    throw new BusinessRuleException("Размер файла не должен превышать 3 МБ");

                await _uniformColorService.EnsureCanCreate(currentUserId);

                await using var stream = file.OpenReadStream();
                var imageUrl = await _imageKitUploader.UploadAsync(stream, file.FileName, "/uniform-colors", cancellationToken);

                var item = await _uniformColorService.Create(
                    new CreateUniformColorDto
                    {
                        Name = name,
                        ImageUrl = imageUrl
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
                _logger.LogError(ex, "Ошибка загрузки цвета формы в ImageKit");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }
    }
}

