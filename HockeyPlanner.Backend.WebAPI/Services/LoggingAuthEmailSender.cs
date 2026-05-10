using HockeyPlanner.Backend.Core.Entities;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class LoggingAuthEmailSender : IAuthEmailSender
    {
        private readonly ILogger<LoggingAuthEmailSender> _logger;

        public LoggingAuthEmailSender(ILogger<LoggingAuthEmailSender> logger)
        {
            _logger = logger;
        }

        public Task SendEmailConfirmation(User user, string token, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Email confirmation token for user {UserId} ({Email}): {Token}",
                user.Id,
                user.Email,
                token);
            return Task.CompletedTask;
        }

        public Task SendPasswordReset(User user, string token, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Password reset token for user {UserId} ({Email}): {Token}",
                user.Id,
                user.Email,
                token);
            return Task.CompletedTask;
        }
    }
}
