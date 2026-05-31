using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.WebAPI.Options;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class ResendAuthEmailSender : IAuthEmailSender
    {
        private const string ResendEmailEndpoint = "https://api.resend.com/emails";
        private readonly EmailOptions _emailOptions;
        private readonly ResendOptions _resendOptions;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ResendAuthEmailSender> _logger;

        public ResendAuthEmailSender(
            IOptions<EmailOptions> emailOptions,
            IOptions<ResendOptions> resendOptions,
            IHttpClientFactory httpClientFactory,
            ILogger<ResendAuthEmailSender> logger)
        {
            _emailOptions = emailOptions.Value;
            _resendOptions = resendOptions.Value;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public Task SendEmailConfirmation(User user, string token, CancellationToken cancellationToken)
        {
            var url = BuildUrl("/confirm-email", "token", token);

            return SendAsync(
                user,
                "Подтверждение почты в Hockey Planner",
                $"Здравствуйте!\n\n" +
                $"Подтвердите почту по ссылке:\n{url}\n\n" +
                $"Если вы не регистрировались в Hockey Planner, просто проигнорируйте это письмо.\n\n" +
                $"Если возникли проблемы или вопросы:\n" +
                $"Telegram: @SergeyUtkinEZ\n" +
                $"Телефон: +7 908 072-30-92",
                cancellationToken);
        }

        public Task SendPasswordReset(User user, string token, CancellationToken cancellationToken)
        {
            var url = BuildUrl("/login", "resetToken", token);

            return SendAsync(
                user,
                "Восстановление пароля Hockey Planner",
                $"Здравствуйте, {user.FirstName}!\n\n" +
                $"Для смены пароля откройте ссылку:\n{url}\n\n" +
                $"Если вы не запрашивали восстановление, просто проигнорируйте это письмо.",
                cancellationToken);
        }

        private async Task SendAsync(User user, string subject, string body, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                _logger.LogWarning("Auth email was not sent because user {UserId} has no email", user.Id);
                return;
            }

            if (string.IsNullOrWhiteSpace(_resendOptions.ApiKey))
            {
                throw new InvalidOperationException("Resend API key is required when Email:Provider is Resend.");
            }

            if (string.IsNullOrWhiteSpace(_emailOptions.FromEmail))
            {
                throw new InvalidOperationException("Email:FromEmail is required when Email:Provider is Resend.");
            }

            var client = _httpClientFactory.CreateClient(nameof(ResendAuthEmailSender));
            using var request = new HttpRequestMessage(HttpMethod.Post, ResendEmailEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _resendOptions.ApiKey);
            request.Content = JsonContent.Create(new ResendEmailRequest
            {
                From = BuildFromAddress(),
                To = [user.Email],
                Subject = subject,
                Text = body
            });

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Resend email request failed with status {(int)response.StatusCode}: {Truncate(responseBody, 500)}");
            }

            _logger.LogInformation("Auth email '{Subject}' sent via Resend to user {UserId} ({Email})", subject, user.Id, user.Email);
        }

        private string BuildFromAddress()
        {
            if (string.IsNullOrWhiteSpace(_emailOptions.FromName))
            {
                return _emailOptions.FromEmail.Trim();
            }

            return $"{_emailOptions.FromName.Trim()} <{_emailOptions.FromEmail.Trim()}>";
        }

        private string BuildUrl(string path, string queryName, string token)
        {
            var baseUrl = _emailOptions.FrontendBaseUrl.TrimEnd('/');
            return $"{baseUrl}{path}?{queryName}={Uri.EscapeDataString(token)}";
        }

        private static string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        private sealed class ResendEmailRequest
        {
            [JsonPropertyName("from")]
            public string From { get; set; } = string.Empty;

            [JsonPropertyName("to")]
            public string[] To { get; set; } = [];

            [JsonPropertyName("subject")]
            public string Subject { get; set; } = string.Empty;

            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;
        }
    }
}
