using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Push;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/push")]
    public class PushNotificationsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly string? _vapidPublicKey;

        public PushNotificationsController(AppDbContext context, IConfiguration configuration)
        {
            _context = context;
            _vapidPublicKey = configuration["VAPID_PUBLIC_KEY"] ?? Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
        }

        [HttpGet("public-key")]
        public ActionResult<object> GetPublicKey()
        {
            if (string.IsNullOrWhiteSpace(_vapidPublicKey))
            {
                return NotFound(new { message = "VAPID public key is not configured." });
            }

            return Ok(new { publicKey = _vapidPublicKey });
        }

        [HttpPost("subscribe")]
        public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint) ||
                string.IsNullOrWhiteSpace(request.Keys?.P256dh) ||
                string.IsNullOrWhiteSpace(request.Keys?.Auth))
            {
                return BadRequest(new { message = "Invalid push subscription payload." });
            }

            var existing = await _context.PushSubscriptions
                .FirstOrDefaultAsync(subscription => subscription.Endpoint == request.Endpoint);

            if (existing is null)
            {
                var subscription = new PushSubscription
                {
                    Endpoint = request.Endpoint.Trim(),
                    P256dhKey = request.Keys.P256dh.Trim(),
                    AuthKey = request.Keys.Auth.Trim(),
                    UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };

                await _context.PushSubscriptions.AddAsync(subscription);
            }
            else
            {
                existing.P256dhKey = request.Keys.P256dh.Trim();
                existing.AuthKey = request.Keys.Auth.Trim();
                existing.UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim();
                existing.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost("unsubscribe")]
        public async Task<IActionResult> Unsubscribe([FromBody] PushUnsubscribeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint))
            {
                return BadRequest(new { message = "Endpoint is required." });
            }

            var existing = await _context.PushSubscriptions
                .FirstOrDefaultAsync(subscription => subscription.Endpoint == request.Endpoint);

            if (existing is not null)
            {
                _context.PushSubscriptions.Remove(existing);
                await _context.SaveChangesAsync();
            }

            return Ok(new { success = true });
        }
    }
}
