using System.Security.Cryptography;
using System.Text;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.WebAPI.Services;

public sealed class AuthEmailOutbox(AppDbContext db, IOptions<JwtOptions> options, TimeProvider clock,
    IAuthTokenService tokens, IAuthEmailSender sender)
{
    // A separate derived encryption key avoids storing recoverable credentials in
    // plaintext or depending on an ephemeral container Data Protection key ring.
    // Pending mail must be drained before rotating the existing signing secret.
    private byte[] Key => HKDF.DeriveKey(HashAlgorithmName.SHA256,
        Encoding.UTF8.GetBytes(options.Value.SigningKey), 32, info: Encoding.UTF8.GetBytes("HockeyPlanner.NotificationEmail.v1"));

    public void Stage(Guid userId, Guid tokenRecordId, string rawToken, NotificationJobKind kind)
    {
        var job = new NotificationJob { UserId = userId, TokenRecordId = tokenRecordId, Kind = kind,
            CreatedAt = clock.GetUtcNow().UtcDateTime, NextAttemptAt = clock.GetUtcNow().UtcDateTime };
        var plaintext = Encoding.UTF8.GetBytes(rawToken);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, job.Id.ToByteArray());
        job.ProtectedPayload = Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
        CryptographicOperations.ZeroMemory(plaintext);
        db.NotificationJobs.Add(job);
    }

    public async Task DeliverAsync(NotificationJob job, CancellationToken token)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = job.Kind == NotificationJobKind.EmailConfirmation
            ? await db.EmailConfirmationTokens.AsNoTracking().Where(value => value.Id == job.TokenRecordId
                && value.UserId == job.UserId && value.UsedAt == null && value.ExpiresAt > now).Select(value => value.TokenHash).SingleOrDefaultAsync(token)
            : await db.PasswordResetTokens.AsNoTracking().Where(value => value.Id == job.TokenRecordId
                && value.UserId == job.UserId && value.UsedAt == null && value.ExpiresAt > now).Select(value => value.TokenHash).SingleOrDefaultAsync(token);
        if (hash is null) return;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(value => value.Id == job.UserId, token);
        if (user is null) return;
        var data = Convert.FromBase64String(job.ProtectedPayload!);
        var plaintext = new byte[data.Length - 28];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plaintext, job.Id.ToByteArray());
        var rawToken = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        if (tokens.HashToken(rawToken) != hash) throw new CryptographicException("Token payload mismatch.");
        if (job.Kind == NotificationJobKind.EmailConfirmation) await sender.SendEmailConfirmation(user, rawToken, token);
        else await sender.SendPasswordReset(user, rawToken, token);
    }
}
