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
            var message = AuthEmailContent.CreateEmailConfirmation(_emailOptions, token);
            return SendAsync(user, message.Subject, message.Body, cancellationToken);
        }

        public Task SendPasswordReset(User user, string token, CancellationToken cancellationToken)
        {
            var message = AuthEmailContent.CreatePasswordReset(_emailOptions, user, token);
            return SendAsync(user, message.Subject, message.Body, cancellationToken);
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
            using var response = await SendRequestAsync(
                client,
                BuildFromAddress(),
                user.Email,
                subject,
                body,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Email provider rejected the request.", null, response.StatusCode);
            }

            _logger.LogInformation("Auth email sent via Resend to user {UserId}", user.Id);
        }

        private async Task<HttpResponseMessage> SendRequestAsync(
            HttpClient client, string from, string to, string subject, string body,
            CancellationToken cancellationToken)
        {
            // Durable retry belongs to the notification worker, not an inner loop.
            using var request = new HttpRequestMessage(HttpMethod.Post, ResendEmailEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _resendOptions.ApiKey);
            request.Content = JsonContent.Create(new ResendEmailRequest
            {
                From = from, To = [to], ReplyTo = NormalizeOptional(_emailOptions.ReplyToEmail),
                Subject = subject, Text = body
            });
            return await client.SendAsync(request, cancellationToken);
        }

        private string BuildFromAddress()
        {
            if (string.IsNullOrWhiteSpace(_emailOptions.FromName))
            {
                return _emailOptions.FromEmail.Trim();
            }

            return $"{_emailOptions.FromName.Trim()} <{_emailOptions.FromEmail.Trim()}>";
        }

        private static string? NormalizeOptional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private sealed class ResendEmailRequest
        {
            [JsonPropertyName("from")]
            public string From { get; set; } = string.Empty;

            [JsonPropertyName("to")]
            public string[] To { get; set; } = [];

            [JsonPropertyName("reply_to")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? ReplyTo { get; set; }

            [JsonPropertyName("subject")]
            public string Subject { get; set; } = string.Empty;

            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;
        }
    }
}
