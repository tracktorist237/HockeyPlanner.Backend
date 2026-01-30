using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Lines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.Web.CodeGenerators.Mvc.Templates.Blazor;


namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    public class LinesController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILineService _lineService;

        public LinesController(AppDbContext context, ILineService lineService)
        {
            _context = context;
            _lineService = lineService;
        }

        [HttpGet]
        [Route("api/lines")]
        public async Task<IActionResult> GetRosterByEvent([FromQuery] Guid eventId)
        {
            var result = await _lineService.GetRosterByEvent(eventId);

            return CreatedAtAction(nameof(GetRosterByEvent), new { id = result }, result);
        }

        [HttpPost]
        [Route("api/lines")]
        public async Task<ActionResult<Line>> CreateRoster([FromBody] CreateRosterRequest request)
        {
            var result = await _lineService.CreateRoster(request);

            return CreatedAtAction(nameof(CreateRoster), new { id = result }, result);
        }

        // PUT: api/Lines/5
        // To protect from overposting attacks, see https://go.microsoft.com/fwlink/?linkid=2123754
        [HttpPut]
        [Route("api/lines")]
        public async Task<IActionResult> PutLine(Guid id, Line line)
        {
            if (id != line.Id)
            {
                return BadRequest();
            }

            _context.Entry(line).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!LineExists(id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return NoContent();
        }



        // DELETE: api/Lines/5
        [HttpDelete]
        [Route("api/lines")]
        public async Task<IActionResult> DeleteLine(Guid id)
        {
            var line = await _context.Lines.FindAsync(id);
            if (line == null)
            {
                return NotFound();
            }

            _context.Lines.Remove(line);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        private bool LineExists(Guid id)
        {
            return _context.Lines.Any(e => e.Id == id);
        }
    }
}
