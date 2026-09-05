using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace HockeyPlanner.Backend.WebAPI.Services;

public interface IExternalLeagueTeamLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(Guid teamId, CancellationToken cancellationToken);
}

public sealed class PostgresTeamSyncLock(AppDbContext context) : IExternalLeagueTeamLock
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(Guid teamId, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        var acquired = await ExecuteAsync("SELECT pg_try_advisory_lock(hashtextextended(@team_id, 0))", teamId, cancellationToken);
        if (!acquired)
        {
            await context.Database.CloseConnectionAsync();
            return null;
        }
        return new Handle(context, teamId);
    }

    private async Task<bool> ExecuteAsync(string sql, Guid teamId, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "team_id";
        parameter.DbType = DbType.String;
        parameter.Value = teamId.ToString("D");
        command.Parameters.Add(parameter);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed class Handle(AppDbContext context, Guid teamId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(hashtextextended(@team_id, 0))";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "team_id";
                parameter.DbType = DbType.String;
                parameter.Value = teamId.ToString("D");
                command.Parameters.Add(parameter);
                await command.ExecuteScalarAsync();
            }
            finally { await context.Database.CloseConnectionAsync(); }
        }
    }
}
