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
                "Authentication email handled by logging sender: type={EmailKind}, user={UserId}.",
                "email confirmation",
                user.Id);
            return Task.CompletedTask;
        }

        public Task SendPasswordReset(User user, string token, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Authentication email handled by logging sender: type={EmailKind}, user={UserId}.",
                "password reset",
                user.Id);
            return Task.CompletedTask;
        }
    }
}
