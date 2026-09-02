using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.WebAPI.Options;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

public sealed class AuthEmailSenderTests
{
    private const string SupportEmail = "support@hockeyplanner.ru";

    [Fact]
    public async Task ResendConfirmationRequest_ContainsConfiguredReplyToAndPreservesTokenUrl()
    {
        const string token = "confirmation token/+=?";
        var options = CreateEmailOptions();
        using var handler = new RecordingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var sender = CreateResendSender(options, client);
        var user = CreateUser();

        await sender.SendEmailConfirmation(user, token, TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = payload.RootElement;
        Assert.Equal("HockeyPlanner <no-reply@hockeyplanner.ru>", root.GetProperty("from").GetString());
        Assert.Equal(user.Email, root.GetProperty("to")[0].GetString());
        Assert.Equal(SupportEmail, root.GetProperty("reply_to").GetString());
        Assert.Equal("Подтверждение почты в HockeyPlanner", root.GetProperty("subject").GetString());
        Assert.Equal(
            $"https://hockeyplanner.ru/confirm-email?token={Uri.EscapeDataString(token)}",
            ExtractUrl(root.GetProperty("text").GetString()));
        AssertSafeSupportCopy(root.GetProperty("text").GetString());
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.resend.com/emails", handler.RequestUri?.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-api-key"), handler.Authorization);
    }

    [Fact]
    public async Task ResendResetRequest_ContainsConfiguredReplyToAndPreservesTokenUrl()
    {
        const string token = "reset token/+=?";
        var options = CreateEmailOptions();
        using var handler = new RecordingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var sender = CreateResendSender(options, client);

        await sender.SendPasswordReset(
            CreateUser(),
            token,
            TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = payload.RootElement;
        Assert.Equal(SupportEmail, root.GetProperty("reply_to").GetString());
        Assert.Equal("Восстановление пароля HockeyPlanner", root.GetProperty("subject").GetString());
        Assert.Equal(
            $"https://hockeyplanner.ru/login?resetToken={Uri.EscapeDataString(token)}",
            ExtractUrl(root.GetProperty("text").GetString()));
        AssertSafeSupportCopy(root.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ResendRequest_OmitsReplyToSafelyWhenNotConfigured()
    {
        var options = CreateEmailOptions();
        options.ReplyToEmail = "  ";
        using var handler = new RecordingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var sender = CreateResendSender(options, client);

        await sender.SendEmailConfirmation(
            CreateUser(),
            "token",
            TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = payload.RootElement;
        Assert.False(root.TryGetProperty("reply_to", out _));
        Assert.DoesNotContain("Если возникли проблемы или вопросы", root.GetProperty("text").GetString());
    }

    [Fact]
    public void SmtpMessage_ContainsConfiguredReplyTo()
    {
        var sender = CreateSmtpSender(CreateEmailOptions());

        var message = sender.CreateMessage(CreateUser(), "subject", "body");

        var from = Assert.Single(message.From.Mailboxes);
        Assert.Equal("HockeyPlanner", from.Name);
        Assert.Equal("no-reply@hockeyplanner.ru", from.Address);
        var replyTo = Assert.Single(message.ReplyTo.Mailboxes);
        Assert.Equal(SupportEmail, replyTo.Address);
    }

    [Fact]
    public void SmtpMessage_HasNoReplyToWhenNotConfigured()
    {
        var options = CreateEmailOptions();
        options.ReplyToEmail = string.Empty;
        var sender = CreateSmtpSender(options);

        var message = sender.CreateMessage(CreateUser(), "subject", "body");

        Assert.Empty(message.ReplyTo);
    }

    [Fact]
    public void EmailOptions_DefaultToPublicProductIdentity()
    {
        var options = new EmailOptions();

        Assert.Equal("HockeyPlanner", options.FromName);
        Assert.Equal(SupportEmail, options.ReplyToEmail);
    }

    private static ResendAuthEmailSender CreateResendSender(
        EmailOptions options,
        HttpClient client) =>
        new(
            Options.Create(options),
            Options.Create(new ResendOptions { ApiKey = "test-api-key" }),
            new SingleClientFactory(client),
            NullLogger<ResendAuthEmailSender>.Instance);

    private static SmtpAuthEmailSender CreateSmtpSender(EmailOptions options) =>
        new(Options.Create(options), NullLogger<SmtpAuthEmailSender>.Instance);

    private static EmailOptions CreateEmailOptions() =>
        new()
        {
            FromEmail = "no-reply@hockeyplanner.ru",
            FromName = "HockeyPlanner",
            ReplyToEmail = SupportEmail,
            FrontendBaseUrl = "https://hockeyplanner.ru/",
        };

    private static User CreateUser() =>
        new()
        {
            FirstName = "Test",
            Email = "user@test.invalid",
        };

    private static string ExtractUrl(string? body) =>
        Assert.IsType<string>(body)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith("https://", StringComparison.Ordinal));

    private static void AssertSafeSupportCopy(string? body)
    {
        var text = Assert.IsType<string>(body);
        Assert.Contains($"Если возникли проблемы или вопросы:\n{SupportEmail}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Telegram", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@SergeyUtkinEZ", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+7 908 072-30-92", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hockey Planner", text, StringComparison.Ordinal);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            RequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
