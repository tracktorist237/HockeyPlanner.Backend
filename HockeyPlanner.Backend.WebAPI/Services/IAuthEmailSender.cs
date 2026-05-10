using HockeyPlanner.Backend.Core.Entities;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface IAuthEmailSender
    {
        Task SendEmailConfirmation(User user, string token, CancellationToken cancellationToken);
        Task SendPasswordReset(User user, string token, CancellationToken cancellationToken);
    }
}
