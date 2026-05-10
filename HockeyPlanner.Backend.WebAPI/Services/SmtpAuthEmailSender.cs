using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.WebAPI.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class SmtpAuthEmailSender : IAuthEmailSender
    {
        private readonly EmailOptions _options;
        private readonly ILogger<SmtpAuthEmailSender> _logger;

        public SmtpAuthEmailSender(IOptions<EmailOptions> options, ILogger<SmtpAuthEmailSender> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public Task SendEmailConfirmation(User user, string token, CancellationToken cancellationToken)
        {
            var url = BuildUrl("/confirm-email", "token", token);
            return SendAsync(
                user,
                "Подтверждение почты в Hockey Planner",
                $"Здравствуйте, {user.FirstName}!\n\nПодтвердите почту по ссылке:\n{url}\n\nЕсли вы не регистрировались в Hockey Planner, просто проигнорируйте это письмо.",
                cancellationToken);
        }

        public Task SendPasswordReset(User user, string token, CancellationToken cancellationToken)
        {
            var url = BuildUrl("/login", "resetToken", token);
            return SendAsync(
                user,
                "Восстановление пароля Hockey Planner",
                $"Здравствуйте, {user.FirstName}!\n\nДля смены пароля откройте ссылку:\n{url}\n\nЕсли вы не запрашивали восстановление, просто проигнорируйте это письмо.",
                cancellationToken);
        }

        private async Task SendAsync(User user, string subject, string body, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                _logger.LogWarning("Auth email was not sent because user {UserId} has no email", user.Id);
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.SmtpHost) ||
                string.IsNullOrWhiteSpace(_options.SmtpUser) ||
                string.IsNullOrWhiteSpace(_options.SmtpPassword))
            {
                throw new InvalidOperationException("SMTP settings are incomplete. Host, user and password are required.");
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_options.FromName, GetFromEmail()));
            message.To.Add(MailboxAddress.Parse(user.Email));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            var secureSocketOptions = _options.SmtpPort == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(
                _options.SmtpHost,
                _options.SmtpPort,
                secureSocketOptions,
                cancellationToken);
            await client.AuthenticateAsync(_options.SmtpUser, _options.SmtpPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            _logger.LogInformation("Auth email '{Subject}' sent to user {UserId} ({Email})", subject, user.Id, user.Email);
        }

        private string BuildUrl(string path, string queryName, string token)
        {
            var baseUrl = _options.FrontendBaseUrl.TrimEnd('/');
            return $"{baseUrl}{path}?{queryName}={Uri.EscapeDataString(token)}";
        }

        private string GetFromEmail()
        {
            if (!string.IsNullOrWhiteSpace(_options.FromEmail))
            {
                return _options.FromEmail;
            }

            return _options.SmtpUser;
        }
    }
}
