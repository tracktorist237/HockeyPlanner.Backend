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
            var message = AuthEmailContent.CreateEmailConfirmation(_options, token);
            return SendAsync(user, message.Subject, message.Body, cancellationToken);
        }

        public Task SendPasswordReset(User user, string token, CancellationToken cancellationToken)
        {
            var message = AuthEmailContent.CreatePasswordReset(_options, user, token);
            return SendAsync(user, message.Subject, message.Body, cancellationToken);
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

            var message = CreateMessage(user, subject, body);

            using var client = new SmtpClient
            {
                Timeout = Math.Max(5, _options.TimeoutSeconds) * 1000
            };
            var secureSocketOptions = ResolveSecureSocketOptions();

            try
            {
                _logger.LogInformation(
                    "Connecting to SMTP {Host}:{Port} with {SecureSocketOptions}, timeout {TimeoutSeconds}s",
                    _options.SmtpHost,
                    _options.SmtpPort,
                    secureSocketOptions,
                    Math.Max(5, _options.TimeoutSeconds));

                await client.ConnectAsync(
                    _options.SmtpHost,
                    _options.SmtpPort,
                    secureSocketOptions,
                    cancellationToken);
                await client.AuthenticateAsync(_options.SmtpUser, _options.SmtpPassword, cancellationToken);
                await client.SendAsync(message, cancellationToken);
                await client.DisconnectAsync(true, cancellationToken);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException(BuildTimeoutMessage(secureSocketOptions), error);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(BuildTimeoutMessage(secureSocketOptions), error);
            }

            _logger.LogInformation("Auth email '{Subject}' sent to user {UserId} ({Email})", subject, user.Id, user.Email);
        }

        internal MimeMessage CreateMessage(User user, string subject, string body)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                throw new ArgumentException("User email is required to create an SMTP message.", nameof(user));
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_options.FromName, GetFromEmail()));
            message.To.Add(MailboxAddress.Parse(user.Email));
            if (!string.IsNullOrWhiteSpace(_options.ReplyToEmail))
            {
                message.ReplyTo.Add(MailboxAddress.Parse(_options.ReplyToEmail.Trim()));
            }

            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };
            return message;
        }

        private SecureSocketOptions ResolveSecureSocketOptions()
        {
            if (!_options.EnableSsl)
            {
                return SecureSocketOptions.None;
            }

            return _options.SmtpPort == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
        }

        private string BuildTimeoutMessage(SecureSocketOptions secureSocketOptions)
        {
            return $"SMTP operation timed out for {_options.SmtpHost}:{_options.SmtpPort} using {secureSocketOptions} after {Math.Max(5, _options.TimeoutSeconds)} seconds.";
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
