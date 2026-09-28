using System.Security.Cryptography;
using System.Text.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HockeyPlanner.Backend.WebAPI.Services;

// Each link commit records its changes. Finalization aggregates the whole team
// operation; a worker can finalize a crashed operation after its session lock dies.
public sealed class LeagueNotificationBatches(AppDbContext db, NotificationOutbox outbox, TimeProvider clock)
{
    private Guid? activeId;
    public async Task<IAsyncDisposable> BeginAsync(Guid teamId, bool background, CancellationToken token)
    {
        if (activeId.HasValue) throw new InvalidOperationException("Nested notification batch.");
        var batch = new LeagueNotificationBatch { TeamId = teamId, Background = background, CreatedAt = clock.GetUtcNow().UtcDateTime };
        var handle = await AcquireAsync(batch.Id, token) ?? throw new InvalidOperationException("New batch lock unavailable.");
        try
        {
            db.LeagueNotificationBatches.Add(batch);
            await db.SaveChangesAsync(token);
            activeId = batch.Id;
            return new BatchHandle(handle, () => activeId = null);
        }
        catch { await handle.DisposeAsync(); throw; }
    }

    public async Task RecordAsync(IReadOnlyCollection<ExternalCreatedEvent> created,
        IReadOnlyCollection<ExternalEventChange> changes, CancellationToken token)
    {
        if (!activeId.HasValue) return;
        // Reload under the event transaction: recovery may have finalized this
        // batch if its session lock was lost during the provider request.
        var batch = await db.LeagueNotificationBatches.FromSqlInterpolated(
            $"SELECT * FROM league_notification_batches WHERE id = {activeId.Value} FOR UPDATE").SingleAsync(token);
        await db.Entry(batch).ReloadAsync(token);
        if (batch.CompletedAt.HasValue) throw new InvalidOperationException("Notification batch already finalized.");
        var items = JsonSerializer.Deserialize<List<BatchChange>>(batch.ChangesJson)!;
        items.AddRange(created.Select(value => new BatchChange(value.EventId, value.Title, true, default)));
        if (batch.Background)
            items.AddRange(changes.Where(value => value.NewStatus == EventStatus.Rescheduled)
                .Select(value => new BatchChange(value.EventId, value.Title, false, value.NewStartTime)));
        batch.ChangesJson = JsonSerializer.Serialize(items);
    }

    public Task CompleteAsync(CancellationToken token) => FinalizeAsync(activeId!.Value, token);

    public async Task RecoverAsync(int take, CancellationToken token)
    {
        var ids = await db.LeagueNotificationBatches.AsNoTracking().Where(value => value.CompletedAt == null)
            .OrderBy(value => value.CreatedAt).Take(take).Select(value => value.Id).ToArrayAsync(token);
        foreach (var id in ids)
        {
            await using var handle = await AcquireAsync(id, token);
            if (handle is not null) await FinalizeAsync(id, token);
        }
    }

    private async Task FinalizeAsync(Guid id, CancellationToken token)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var batch = await db.LeagueNotificationBatches.FromSqlInterpolated(
            $"SELECT * FROM league_notification_batches WHERE id = {id} FOR UPDATE").SingleAsync(token);
        if (batch.CompletedAt.HasValue) return;
        var changes = JsonSerializer.Deserialize<List<BatchChange>>(batch.ChangesJson)!;
        var users = await db.TeamMemberships.AsNoTracking().Where(value => value.TeamId == batch.TeamId)
            .Select(value => value.UserId).Distinct().ToArrayAsync(token);
        var created = changes.Where(value => value.Created).DistinctBy(value => value.EventId).ToArray();
        if (created.Length > 0)
            await outbox.EnqueueAsync(users, $"league:{id}:created", NotificationType.EventPublished,
                NotificationCategory.AttendanceRequired, created.Length == 1 ? "Новое мероприятие" : "Новые мероприятия",
                created.Length == 1 ? $"{created[0].Title}: отметьтесь, сможете ли быть."
                    : $"Появилось {created.Length} новых мероприятий из лиги. Отметьтесь, сможете ли быть.",
                created.Length == 1 ? $"/events/{created[0].EventId}" : "/events", token);
        foreach (var change in changes.Where(value => !value.Created).DistinctBy(value => value.EventId))
            await outbox.EnqueueAsync(users, $"league:{id}:rescheduled:{change.EventId}", NotificationType.EventRescheduled,
                NotificationCategory.EventUpdates, "Матч перенесён",
                ExternalLeagueBackgroundSyncWorker.BuildRescheduledBody(new ExternalEventChange
                    { EventId = change.EventId, Title = change.Title, NewStartTime = change.StartTime }),
                $"/events/{change.EventId}", token);
        batch.CompletedAt = clock.GetUtcNow().UtcDateTime;
        batch.ChangesJson = "[]";
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task<IAsyncDisposable?> AcquireAsync(Guid id, CancellationToken token)
    {
        var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        try
        {
            await connection.OpenAsync(token);
            var key = BitConverter.ToInt64(SHA256.HashData(id.ToByteArray()), 0);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            if ((bool)(await command.ExecuteScalarAsync(token))!) return new LockHandle(connection, key);
            await connection.DisposeAsync();
            return null;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class BatchHandle(IAsyncDisposable connection, Action release) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { release(); await connection.DisposeAsync(); }
    }
    private sealed class LockHandle(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection) { CommandTimeout = 5 };
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteNonQueryAsync();
            }
            catch { NpgsqlConnection.ClearPool(connection); throw; }
            finally { await connection.DisposeAsync(); }
        }
    }
    private sealed record BatchChange(Guid EventId, string Title, bool Created, DateTime StartTime);
}
