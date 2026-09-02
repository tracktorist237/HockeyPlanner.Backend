using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.WebAPI.Options;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    internal sealed record AuthEmailMessage(string Subject, string Body);

    internal static class AuthEmailContent
    {
        public static AuthEmailMessage CreateEmailConfirmation(
            EmailOptions options,
            string token)
        {
            var url = BuildUrl(options, "/confirm-email", "token", token);
            return new AuthEmailMessage(
                "Подтверждение почты в HockeyPlanner",
                $"Здравствуйте!\n\n" +
                $"Подтвердите почту по ссылке:\n{url}\n\n" +
                $"Если вы не регистрировались в HockeyPlanner, просто проигнорируйте это письмо." +
                BuildSupportContact(options));
        }

        public static AuthEmailMessage CreatePasswordReset(
            EmailOptions options,
            User user,
            string token)
        {
            var url = BuildUrl(options, "/login", "resetToken", token);
            return new AuthEmailMessage(
                "Восстановление пароля HockeyPlanner",
                $"Здравствуйте, {user.FirstName}!\n\n" +
                $"Для смены пароля откройте ссылку:\n{url}\n\n" +
                $"Если вы не запрашивали восстановление, просто проигнорируйте это письмо." +
                BuildSupportContact(options));
        }

        private static string BuildSupportContact(EmailOptions options) =>
            string.IsNullOrWhiteSpace(options.ReplyToEmail)
                ? string.Empty
                : $"\n\nЕсли возникли проблемы или вопросы:\n{options.ReplyToEmail.Trim()}";

        private static string BuildUrl(
            EmailOptions options,
            string path,
            string queryName,
            string token)
        {
            var baseUrl = options.FrontendBaseUrl.TrimEnd('/');
            return $"{baseUrl}{path}?{queryName}={Uri.EscapeDataString(token)}";
        }
    }
}
