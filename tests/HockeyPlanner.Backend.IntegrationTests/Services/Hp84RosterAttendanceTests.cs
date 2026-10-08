using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Shared.Models.Events;
using HockeyPlanner.Backend.Shared.Models.Lines;
using HockeyPlanner.Backend.WebAPI.Models.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "HP84")]
public sealed class Hp84RosterAttendanceTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task DuplicateEffectiveRoster_ReturnsSafe400_AndPreservesWholeRoster(bool guest, bool acrossLines, bool replace)
    {
        var scenario = await Scenario();
        var id = guest ? (await Guest(scenario)).Id : scenario.UserA.Id;
        var request = Request(scenario.EventB.Id, id, guest);
        if (acrossLines) request.Lines.Add(Request(scenario.EventB.Id, id, guest).Lines.Single());
        else request.Lines[0].Players.Add(request.Lines[0].Players[0]);
        using var client = AuthenticatedTestClientFactory.Create(factory, scenario.UserB);
        using var response = replace
            ? await client.PutAsJsonAsync("/api/lines", request, Token)
            : await client.PostAsJsonAsync("/api/lines", request, Token);
        await AssertProblem(response, HttpStatusCode.BadRequest);
        await AssertOriginalRoster(scenario);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedCreate_IsSafe409_WithoutPartialLines_AndContextCanBeReused(bool guest)
    {
        var scenario = await Scenario();
        var id = guest ? (await Guest(scenario)).Id : scenario.UserB.Id;
        if (guest)
        {
            using var initial = AuthenticatedTestClientFactory.Create(factory, scenario.UserB);
            using var created = await initial.PostAsJsonAsync("/api/lines", Request(scenario.EventB.Id, id, true), Token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        using var client = AuthenticatedTestClientFactory.Create(factory, scenario.UserB);
        using var rejected = await client.PostAsJsonAsync("/api/lines", Request(scenario.EventB.Id, id, guest), Token);
        await AssertProblem(rejected, HttpStatusCode.Conflict);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ILineService>();
        await Assert.ThrowsAsync<HockeyPlanner.Backend.Core.Exceptions.ConflictException>(() =>
            service.CreateRoster(Request(scenario.EventB.Id, id, guest), scenario.UserB.Id, Token));
        await db.SaveChangesAsync(Token); // Rejected additions must not survive in the tracker.
        Assert.Equal(guest ? 2 : 1, await db.Lines.CountAsync(l => l.EventId == scenario.EventB.Id, Token));
        Assert.True(await db.Players.AnyAsync(p => p.Id == scenario.PlayerB.Id, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSql_OverlappingTransactions_RejectDuplicateAndRetainWinnerAndPriorRoster(bool guest)
    {
        var scenario = await Scenario();
        var identity = guest ? (await Guest(scenario)).Id : scenario.UserA.Id;
        await using var first = Context();
        await using var second = Context();
        await first.Database.OpenConnectionAsync(Token);
        await second.Database.OpenConnectionAsync(Token);
        var firstPid = ((NpgsqlConnection)first.Database.GetDbConnection()).ProcessID;
        var secondPid = ((NpgsqlConnection)second.Database.GetDbConnection()).ProcessID;
        Assert.NotEqual(firstPid, secondPid);
        await using var firstTx = await first.Database.BeginTransactionAsync(Token);
        await using var secondTx = await second.Database.BeginTransactionAsync(Token);
        var winner = Line(scenario.EventB.Id, identity, guest);
        var loser = Line(scenario.EventB.Id, identity, guest);
        first.Lines.Add(winner);
        second.Lines.Add(loser);
        await first.SaveChangesAsync(Token); // Explicit synchronization: winner remains uncommitted.
        var attempted = second.SaveChangesAsync(Token);
        try
        {
            // Prove the second PostgreSQL transaction actually overlaps and blocks
            // on the first; no timing guesses or retries of a failed write.
            await WaitForBlock(secondPid, firstPid, attempted);
            await firstTx.CommitAsync(Token);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => attempted);
            Assert.True(RosterConstraints.IsDuplicate(failure));
            await secondTx.RollbackAsync(Token);
        }
        finally
        {
            if (!attempted.IsCompleted)
            {
                await firstTx.RollbackAsync(Token);
                try { await attempted; } catch (DbUpdateException) { }
            }
        }
        await using var verification = Context();
        var persisted = await verification.Players.Where(p => p.EventId == scenario.EventB.Id).ToListAsync(Token);
        Assert.Equal(2, persisted.Count);
        Assert.Contains(persisted, p => p.Id == scenario.PlayerB.Id);
        Assert.Contains(persisted, p => p.Id == winner.Players.Single().Id);
        Assert.DoesNotContain(persisted, p => p.Id == loser.Players.Single().Id);
        Assert.False(await verification.Lines.AnyAsync(l => l.Id == loser.Id, Token));
    }

    [Fact]
    public async Task PostgreSql_AllowsUserAcrossEvents_DistinctGuests_AndSeparateUserGuestIdentitySpaces()
    {
        var scenario = await Scenario();
        var sameIdGuest = await Guest(scenario, id: scenario.UserA.Id);
        var otherGuest = await Guest(scenario);
        await using var db = Context();
        var otherEvent = Event(scenario.TeamB.Id);
        db.AddRange(otherEvent, Line(scenario.EventB.Id, scenario.UserA.Id), Line(otherEvent.Id, scenario.UserA.Id),
            Line(scenario.EventB.Id, sameIdGuest.Id, true), Line(scenario.EventB.Id, otherGuest.Id, true));
        await db.SaveChangesAsync(Token);
        await using var read = Context();
        Assert.Equal(2, await read.Players.CountAsync(p => p.UserId == scenario.UserA.Id, Token));
        Assert.Equal(2, await read.Players.CountAsync(p => p.EventId == scenario.EventB.Id && p.EventGuestId != null, Token));
        // Application validation also distinguishes a user from a guest with the same UUID.
        using var client = AuthenticatedTestClientFactory.Create(factory, scenario.UserB);
        var request = Request(scenario.EventB.Id, scenario.UserA.Id);
        request.Lines.Add(Request(scenario.EventB.Id, sameIdGuest.Id, true).Lines.Single());
        using var response = await client.PutAsJsonAsync("/api/lines", request, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("line_event")]
    [InlineData("guest_event")]
    [InlineData("update_player")]
    [InlineData("move_line")]
    [InlineData("move_guest")]
    public async Task PostgreSql_RejectsForgedEventRelationshipsOnInsertAndUpdate(string mutation)
    {
        var scenario = await Scenario();
        var guest = await Guest(scenario);
        await using var db = Context();
        var otherEvent = Event(scenario.TeamB.Id);
        var guestLine = Line(scenario.EventB.Id, guest.Id, true);
        db.AddRange(otherEvent, guestLine);
        await db.SaveChangesAsync(Token);
        if (mutation is "line_event" or "guest_event")
        {
            var player = new Player { LineId = scenario.LineB.Id, EventId = mutation == "line_event" ? otherEvent.Id : scenario.EventB.Id,
                UserId = mutation == "line_event" ? scenario.UserA.Id : null,
                EventGuestId = mutation == "guest_event" ? guest.Id : null };
            if (mutation == "guest_event")
            {
                var foreignLine = new Line { EventId = otherEvent.Id, Name = "Foreign" };
                db.Lines.Add(foreignLine);
                await db.SaveChangesAsync(Token);
                player.LineId = foreignLine.Id;
                player.EventId = otherEvent.Id;
            }
            db.Players.Add(player);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => mutation switch
            {
                "update_player" => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE players SET event_id = {otherEvent.Id} WHERE id = {scenario.PlayerB.Id}", Token),
                "move_line" => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE lines SET event_id = {otherEvent.Id} WHERE id = {scenario.LineB.Id}", Token),
                _ => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE event_guests SET event_id = {otherEvent.Id} WHERE id = {guest.Id}", Token)
            });
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        }
        await using var read = Context();
        Assert.Equal(scenario.EventB.Id, (await read.Players.SingleAsync(p => p.Id == scenario.PlayerB.Id, Token)).EventId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclinedDuplicates_AreFilteredBeforeValidation_AndForeignGuestFailsWithoutReplacement(bool guest)
    {
        var scenario = await Scenario();
        var declined = guest ? await Guest(scenario, AttendanceStatus.Declined) : null;
        await using (var db = Context())
        {
            if (!guest) db.Attendances.Add(new Attendance { EventId = scenario.EventB.Id, UserId = scenario.UserA.Id, Status = AttendanceStatus.Declined });
            await db.SaveChangesAsync(Token);
        }
        var request = Request(scenario.EventB.Id, declined?.Id ?? scenario.UserA.Id, guest);
        request.Lines.Add(Request(scenario.EventB.Id, declined?.Id ?? scenario.UserA.Id, guest).Lines.Single());
        using var client = AuthenticatedTestClientFactory.Create(factory, scenario.UserB);
        using var response = await client.PutAsJsonAsync("/api/lines", request, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var lines = await response.Content.ReadFromJsonAsync<List<LineDto>>(Token);
        Assert.Equal(2, lines!.Count);
        Assert.All(lines, l => Assert.Empty(l.Members));
        var foreignGuest = await Guest(scenario, eventId: (await AddEvent(scenario.TeamA.Id)).Id);
        using var foreign = await client.PutAsJsonAsync("/api/lines", Request(scenario.EventB.Id, foreignGuest.Id, true), Token);
        await AssertProblem(foreign, HttpStatusCode.NotFound);
        await using var read = Context();
        Assert.Equal(lines.Select(l => l.Id).Order(), await read.Lines.Where(l => l.EventId == scenario.EventB.Id).Select(l => l.Id).OrderBy(id => id).ToListAsync(Token));
    }

    [Theory]
    [InlineData(AttendanceStatus.Declined, false)]
    [InlineData(AttendanceStatus.Pending, false)]
    [InlineData(AttendanceStatus.Confirmed, false)]
    [InlineData(AttendanceStatus.Declined, true)]
    [InlineData(AttendanceStatus.Pending, true)]
    [InlineData(AttendanceStatus.Confirmed, true)]
    public async Task Attendance_SavePersistsStatusNotesAndCleanup_WithOrWithoutExistingAttendance(AttendanceStatus status, bool absent)
    {
        var scenario = await Scenario();
        if (absent)
        {
            await using var setup = Context();
            await setup.Attendances.Where(a => a.Id == scenario.AttendanceB.Id).ExecuteDeleteAsync(Token);
        }
        using var client = AuthenticatedTestClientFactory.Create(factory, scenario.UserB);
        using var response = await client.PostAsJsonAsync($"/api/events/{scenario.EventB.Id}/attendance/{scenario.UserB.Id}",
            new UpdateAttendanceRequest { Status = status, Notes = "HP84 answer", IgnoreConflicts = true }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = Context();
        var attendance = await read.Attendances.SingleAsync(a => a.EventId == scenario.EventB.Id && a.UserId == scenario.UserB.Id, Token);
        Assert.Equal(status, attendance.Status);
        Assert.Equal("HP84 answer", attendance.Notes);
        Assert.NotNull(attendance.UpdatedAt);
        Assert.Equal(attendance.UpdatedAt, attendance.RespondedAt);
        Assert.Equal(status == AttendanceStatus.Confirmed, await read.Players.AnyAsync(p => p.Id == scenario.PlayerB.Id, Token));
        // Repeat after cleanup, exercising the no-player path.
        using var repeated = await client.PostAsJsonAsync($"/api/events/{scenario.EventB.Id}/attendance/{scenario.UserB.Id}",
            new UpdateAttendanceRequest { Status = status, IgnoreConflicts = true }, Token);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
    }

    [Theory]
    [InlineData(AttendanceStatus.Declined, false, false)]
    [InlineData(AttendanceStatus.Pending, false, false)]
    [InlineData(AttendanceStatus.Declined, true, false)]
    [InlineData(AttendanceStatus.Pending, true, false)]
    [InlineData(AttendanceStatus.Declined, false, true)]
    [InlineData(AttendanceStatus.Pending, true, true)]
    public async Task ActualSqlWriteThenInjectedFailureOrCancellation_RollsBackAnswerAndPlayer(AttendanceStatus status, bool guest, bool cancel)
    {
        var scenario = await Scenario();
        var targetGuest = guest ? await Guest(scenario) : null;
        if (targetGuest is not null)
        {
            await using var setup = Context();
            setup.Lines.Add(Line(scenario.EventB.Id, targetGuest.Id, true));
            await setup.SaveChangesAsync(Token);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var fault = new FaultAfterWrite(guest ? "UPDATE event_guests" : "UPDATE attendances", cancel ? cancellation : null);
        await using var scope = factory.Services.CreateAsyncScope();
        await using var db = Context(fault);
        var service = (IEventService)ActivatorUtilities.CreateInstance(scope.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<IEventService>().GetType(), db);
        Func<Task> operation = guest
            ? () => service.UpdateEventGuestAttendance(scenario.EventB.Id, targetGuest!.Id, new() { Status = status, Notes = "Failed answer" }, scenario.UserB.Id, cancellation.Token)
            : () => service.UpdateAttendance(scenario.EventB.Id, scenario.UserB.Id, new() { Status = status, Notes = "Failed answer" }, scenario.UserB.Id, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
        else
        {
            var failure = await Assert.ThrowsAsync<DbUpdateException>(operation);
            Assert.Equal("HP84 injected failure after actual SQL write", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        }
        Assert.True(fault.WriteExecuted);
        Assert.Equal(status, fault.ObservedStatus);
        Assert.Equal(0, fault.PlayerDeletes);
        await using var read = Context();
        if (guest)
        {
            var answer = await read.EventGuests.SingleAsync(g => g.Id == targetGuest!.Id, Token);
            Assert.Equal(AttendanceStatus.Confirmed, answer.Status);
            Assert.Equal(targetGuest!.RespondedAt.Ticks / 10, answer.RespondedAt.Ticks / 10);
            Assert.Null(answer.Notes);
            Assert.Null(answer.UpdatedAt);
            Assert.True(await read.Players.AnyAsync(p => p.EventGuestId == answer.Id, Token));
        }
        else
        {
            var answer = await read.Attendances.SingleAsync(a => a.Id == scenario.AttendanceB.Id, Token);
            Assert.Equal(AttendanceStatus.Confirmed, answer.Status);
            Assert.Equal(scenario.AttendanceB.RespondedAt.Ticks / 10, answer.RespondedAt.Ticks / 10);
            Assert.Null(answer.UpdatedAt);
            Assert.Null(answer.Notes);
            Assert.True(await read.Players.AnyAsync(p => p.Id == scenario.PlayerB.Id, Token));
            // Reusing the failed context must not commit failed tracked intent.
            await db.SaveChangesAsync(Token);
            read.ChangeTracker.Clear();
            Assert.Equal(AttendanceStatus.Confirmed, (await read.Attendances.SingleAsync(a => a.Id == answer.Id, Token)).Status);
        }
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("foreign", HttpStatusCode.Forbidden)]
    [InlineData("member", HttpStatusCode.Forbidden)]
    [InlineData("admin", HttpStatusCode.Created)]
    public async Task RosterMutation_UsesJwtRole_AndIgnoresSpoofedActor(string actor, HttpStatusCode expected)
    {
        var scenario = await Scenario();
        if (actor is "member" or "admin")
        {
            await using var setup = Context();
            await setup.TeamMemberships.Where(m => m.TeamId == scenario.TeamB.Id && m.UserId == scenario.UserB.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, actor == "admin" ? TeamMemberRole.Admin : TeamMemberRole.Member), Token);
        }
        using var client = actor == "anonymous" ? factory.CreateClient() :
            AuthenticatedTestClientFactory.Create(factory, actor == "foreign" ? scenario.UserA : scenario.UserB);
        using var response = await client.PutAsJsonAsync($"/api/lines?currentUserId={scenario.UserB.Id}", Request(scenario.EventB.Id, scenario.UserB.Id), Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.Created) await AssertOriginalRoster(scenario);
    }

    [Fact]
    public async Task Transfer_GuestNameMergeCollision_IsSafe409_AndRollsBackTargetRosterAndAllOtherChanges()
    {
        var scenario = await Scenario();
        var source = await AddEvent(scenario.TeamB.Id);
        var first = await Guest(scenario, eventId: source.Id);
        var second = await Guest(scenario, eventId: source.Id);
        await using (var setup = Context())
        {
            await setup.EventGuests.Where(g => g.Id == first.Id || g.Id == second.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.FirstName, "Same").SetProperty(g => g.LastName, "Guest"), Token);
            setup.Lines.AddRange(Line(source.Id, first.Id, true), Line(source.Id, second.Id, true));
            await setup.Events.Where(e => e.Id == source.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Description, "Must not transfer"), Token);
            await setup.SaveChangesAsync(Token);
        }
        using var client = AuthenticatedTestClientFactory.Create(factory, scenario.UserB);
        using var response = await client.PostAsJsonAsync($"/api/events/{source.Id}/transfer", new TransferEventDataRequest
        { TargetEventId = scenario.EventB.Id, Roster = true, Guests = true, Description = true, DeleteSourceEvent = true }, Token);
        await AssertProblem(response, HttpStatusCode.Conflict);
        await AssertOriginalRoster(scenario);
        await using var read = Context();
        Assert.True(await read.Events.AnyAsync(e => e.Id == source.Id, Token));
        Assert.Null((await read.Events.SingleAsync(e => e.Id == scenario.EventB.Id, Token)).Description);
        Assert.Equal(2, await read.EventGuests.CountAsync(g => g.EventId == source.Id, Token));
        Assert.False(await read.EventGuests.AnyAsync(g => g.EventId == scenario.EventB.Id, Token));
    }

    [Fact]
    public async Task RosterReplacement_FailureAfterNestedOutboxSqlSave_RollsBackRosterNotificationAndJob()
    {
        var scenario = await Scenario();
        await using var scope = factory.Services.CreateAsyncScope();
        var fault = new FaultAfterWrite("INSERT INTO notification_jobs", null);
        await using var db = Context(fault);
        // Build all services against the intercepted context, including the existing outbox.
        var outbox = ActivatorUtilities.CreateInstance<NotificationOutbox>(scope.ServiceProvider, db);
        var notifications = ActivatorUtilities.CreateInstance<HockeyPlanner.Backend.WebAPI.Services.NotificationService>(scope.ServiceProvider, db, outbox);
        var service = (ILineService)ActivatorUtilities.CreateInstance(scope.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<ILineService>().GetType(), db, notifications);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.UpdateRoster(Request(scenario.EventB.Id, scenario.UserA.Id), scenario.UserB.Id, Token));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.True(fault.WriteExecuted);
        await AssertOriginalRoster(scenario);
        await using var read = Context();
        Assert.False(await read.Notifications.AnyAsync(n => n.Url == $"/events/{scenario.EventB.Id}?tab=roster", Token));
        Assert.False(await read.NotificationJobs.AnyAsync(j => j.Notification!.Url == $"/events/{scenario.EventB.Id}?tab=roster", Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewAttendance_InsertThenFailure_RollsBackAndClearsOnlyFailedIntent(bool cancel)
    {
        var scenario = await Scenario();
        await using (var setup = Context())
            await setup.Attendances.Where(a => a.Id == scenario.AttendanceB.Id).ExecuteDeleteAsync(Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var fault = new FaultAfterWrite("INSERT INTO attendances", cancel ? cancellation : null);
        await using var db = Context(fault);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = (IEventService)ActivatorUtilities.CreateInstance(scope.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<IEventService>().GetType(), db);
        Func<Task> operation = () => service.UpdateAttendance(scenario.EventB.Id, scenario.UserB.Id,
            new() { Status = AttendanceStatus.Declined }, scenario.UserB.Id, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
        else await Assert.ThrowsAsync<DbUpdateException>(operation);
        Assert.True(fault.WriteExecuted);
        await db.SaveChangesAsync(Token);
        await using var read = Context();
        Assert.False(await read.Attendances.AnyAsync(a => a.EventId == scenario.EventB.Id, Token));
        Assert.True(await read.Players.AnyAsync(p => p.Id == scenario.PlayerB.Id, Token));
    }

    [Fact]
    public async Task Attendance_ConflictWarningPrecedesMutation_AndOverrideWorksInReusedContext()
    {
        var scenario = await Scenario();
        var conflict = new ScheduledEvent { TeamId = scenario.TeamB.Id, Title = "Overlap",
            StartTime = scenario.EventB.StartTime, DurationMinutes = 60 };
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Events.Add(conflict);
        db.Attendances.Add(new Attendance { EventId = conflict.Id, UserId = scenario.UserB.Id, Status = AttendanceStatus.Confirmed });
        await db.Attendances.Where(a => a.Id == scenario.AttendanceB.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AttendanceStatus.Pending), Token);
        await db.SaveChangesAsync(Token);
        db.ChangeTracker.Clear();
        var service = scope.ServiceProvider.GetRequiredService<IEventService>();
        var warnings = await service.UpdateAttendance(scenario.EventB.Id, scenario.UserB.Id,
            new() { Status = AttendanceStatus.Confirmed, Notes = "Must not save yet" }, scenario.UserB.Id, Token);
        Assert.Contains(warnings, w => w.Id == conflict.Id);
        await using (var read = Context())
        {
            var original = await read.Attendances.SingleAsync(a => a.Id == scenario.AttendanceB.Id, Token);
            Assert.Equal(AttendanceStatus.Pending, original.Status);
            Assert.Null(original.Notes);
            Assert.True(await read.Players.AnyAsync(p => p.Id == scenario.PlayerB.Id, Token));
        }
        Assert.Empty(await service.UpdateAttendance(scenario.EventB.Id, scenario.UserB.Id,
            new() { Status = AttendanceStatus.Confirmed, IgnoreConflicts = true }, scenario.UserB.Id, Token));
        Assert.Equal(AttendanceStatus.Confirmed, (await db.Attendances.SingleAsync(a => a.Id == scenario.AttendanceB.Id, Token)).Status);
        Assert.Empty(await service.UpdateAttendance(scenario.EventB.Id, scenario.UserB.Id,
            new() { Status = AttendanceStatus.Pending }, scenario.UserB.Id, Token));
        Assert.Equal(AttendanceStatus.Pending, (await db.Attendances.SingleAsync(a => a.Id == scenario.AttendanceB.Id, Token)).Status);
    }

    [Theory]
    [InlineData("anonymous", false, HttpStatusCode.Unauthorized)]
    [InlineData("foreign", false, HttpStatusCode.Forbidden)]
    [InlineData("foreign", true, HttpStatusCode.Forbidden)]
    [InlineData("foreign_guest", true, HttpStatusCode.NotFound)]
    public async Task Attendance_DeniedMutationsLeaveAnswerAndRosterIntact(string actor, bool guest, HttpStatusCode expected)
    {
        var scenario = await Scenario();
        var target = guest ? await Guest(scenario, eventId: actor == "foreign_guest" ? (await AddEvent(scenario.TeamA.Id)).Id : null) : null;
        using var client = actor == "anonymous" ? factory.CreateClient() :
            AuthenticatedTestClientFactory.Create(factory, actor == "foreign" ? scenario.UserA : scenario.UserB);
        var path = guest ? $"/api/events/{scenario.EventB.Id}/guests/{target!.Id}/attendance" :
            $"/api/events/{scenario.EventB.Id}/attendance/{scenario.UserB.Id}";
        using var response = await client.PostAsJsonAsync($"{path}?currentUserId={scenario.UserB.Id}", new UpdateAttendanceRequest { Status = AttendanceStatus.Declined }, Token);
        Assert.Equal(expected, response.StatusCode);
        await AssertOriginalRoster(scenario);
        await using var read = Context();
        Assert.Equal(AttendanceStatus.Confirmed, (await read.Attendances.SingleAsync(a => a.Id == scenario.AttendanceB.Id, Token)).Status);
        if (target is not null) Assert.Equal(AttendanceStatus.Confirmed, (await read.EventGuests.SingleAsync(g => g.Id == target.Id, Token)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSql_SameLineDuplicateIsRejected_WithoutRemovingExistingPlayer(bool guest)
    {
        var scenario = await Scenario();
        var identity = guest ? (await Guest(scenario)).Id : scenario.UserB.Id;
        var lineId = scenario.LineB.Id;
        await using (var setup = Context())
        {
            if (guest)
            {
                var line = Line(scenario.EventB.Id, identity, true);
                setup.Lines.Add(line);
                await setup.SaveChangesAsync(Token);
                lineId = line.Id;
            }
        }
        await using var write = Context();
        write.Players.Add(new Player { EventId = scenario.EventB.Id, LineId = lineId,
            UserId = guest ? null : identity, EventGuestId = guest ? identity : null });
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => write.SaveChangesAsync(Token));
        Assert.True(RosterConstraints.IsDuplicate(failure));
        await using var read = Context();
        Assert.Equal(1, await read.Players.CountAsync(p => p.LineId == lineId, Token));
        Assert.True(await read.Players.AnyAsync(p => p.Id == scenario.PlayerB.Id, Token));
    }

    [Theory]
    [InlineData(AttendanceStatus.Confirmed)]
    [InlineData(AttendanceStatus.Pending)]
    [InlineData(AttendanceStatus.Declined)]
    public async Task GuestAttendance_SuccessPreservesStatusAndCleanupContract(AttendanceStatus status)
    {
        var scenario = await Scenario();
        var guest = await Guest(scenario);
        await using (var setup = Context())
        {
            setup.Lines.Add(Line(scenario.EventB.Id, guest.Id, true));
            await setup.SaveChangesAsync(Token);
        }
        using var client = AuthenticatedTestClientFactory.Create(factory, scenario.UserB);
        using var response = await client.PostAsJsonAsync($"/api/events/{scenario.EventB.Id}/guests/{guest.Id}/attendance",
            new UpdateAttendanceRequest { Status = status, Notes = "Guest answer" }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = Context();
        var answer = await read.EventGuests.SingleAsync(g => g.Id == guest.Id, Token);
        Assert.Equal(status, answer.Status);
        Assert.Equal("Guest answer", answer.Notes);
        Assert.Equal(answer.UpdatedAt, answer.RespondedAt);
        Assert.Equal(status == AttendanceStatus.Confirmed, await read.Players.AnyAsync(p => p.EventGuestId == guest.Id, Token));
    }

    [Fact]
    public async Task PostgreSql_LineCascadeAndGuestRestrictRemainEffective()
    {
        var scenario = await Scenario();
        var guest = await Guest(scenario);
        await using var db = Context();
        var line = Line(scenario.EventB.Id, guest.Id, true);
        db.Lines.Add(line);
        await db.SaveChangesAsync(Token);
        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            db.EventGuests.Where(g => g.Id == guest.Id).ExecuteDeleteAsync(Token));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, denied.SqlState);
        Assert.Equal(1, await db.Lines.Where(l => l.Id == line.Id).ExecuteDeleteAsync(Token));
        await using var read = Context();
        Assert.False(await read.Players.AnyAsync(p => p.LineId == line.Id, Token));
        Assert.True(await read.EventGuests.AnyAsync(g => g.Id == guest.Id, Token));
        Assert.True(await read.Players.AnyAsync(p => p.Id == scenario.PlayerB.Id, Token));
    }

    private Task<TwoTeamSecurityScenario> Scenario() => TwoTeamSecurityScenarioBuilder.CreateAsync(factory.Services, Token);

    private AppDbContext Context(IInterceptor? interceptor = null)
    {
        using var scope = factory.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection, p => p.MaxBatchSize(1));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new AppDbContext(options.Options);
    }

    private async Task<EventGuest> Guest(TwoTeamSecurityScenario scenario, AttendanceStatus status = AttendanceStatus.Confirmed, Guid? eventId = null, Guid? id = null)
    {
        await using var db = Context();
        var guest = new EventGuest { EventId = eventId ?? scenario.EventB.Id, InvitedByUserId = scenario.UserB.Id,
            FirstName = "Guest", LastName = Guid.NewGuid().ToString("N"), Status = status };
        if (id.HasValue) db.Entry(guest).Property(g => g.Id).CurrentValue = id.Value;
        db.EventGuests.Add(guest);
        await db.SaveChangesAsync(Token);
        return guest;
    }

    private static Line Line(Guid eventId, Guid id, bool guest = false) => new()
    {
        EventId = eventId, Name = "HP84 line", Players = [new Player { EventId = eventId,
            UserId = guest ? null : id, EventGuestId = guest ? id : null, Role = PlayerRole.Center }]
    };

    private static ScheduledEvent Event(Guid teamId) => new()
    { TeamId = teamId, Title = "HP84 event", StartTime = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), DurationMinutes = 60 };

    private async Task<ScheduledEvent> AddEvent(Guid teamId)
    {
        await using var db = Context();
        var value = Event(teamId);
        db.Events.Add(value);
        await db.SaveChangesAsync(Token);
        return value;
    }

    private static CreateUpdateRosterRequest Request(Guid eventId, Guid id, bool guest = false) => new()
    { EventId = eventId, Lines = [new() { Name = "HP84", Players = [new() { UserId = id, IsGuest = guest, Role = PlayerRole.Center }] }] };

    private async Task AssertOriginalRoster(TwoTeamSecurityScenario scenario)
    {
        await using var read = Context();
        Assert.Equal(scenario.LineB.Id, (await read.Lines.SingleAsync(l => l.EventId == scenario.EventB.Id, Token)).Id);
        Assert.Equal(scenario.PlayerB.Id, (await read.Players.SingleAsync(p => p.EventId == scenario.EventB.Id, Token)).Id);
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var root = body.RootElement;
        Assert.Equal((int)status, root.GetProperty("status").GetInt32());
        Assert.Equal(root.GetProperty("detail").GetString(), root.GetProperty("message").GetString());
        Assert.Equal(root.GetProperty("detail").GetString(), root.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("ux_players", root.ToString());
        Assert.DoesNotContain("Npgsql", root.ToString());
    }

    private async Task WaitForBlock(int waiter, int blocker, Task attempted)
    {
        await using var monitor = Context();
        await monitor.Database.OpenConnectionAsync(Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            Assert.False(attempted.IsCompleted, "The losing operation must wait on the uncommitted winner.");
            await using var query = monitor.Database.GetDbConnection().CreateCommand();
            query.CommandText = "SELECT pg_blocking_pids(@pid)";
            query.Parameters.Add(new NpgsqlParameter("pid", waiter));
            if (((int[])(await query.ExecuteScalarAsync(deadline.Token))!).Contains(blocker)) return;
        }
    }

    private sealed class FaultAfterWrite(string commandPrefix, CancellationTokenSource? cancellation) : DbCommandInterceptor
    {
        public bool WriteExecuted { get; private set; }
        public AttendanceStatus? ObservedStatus { get; private set; }
        public int PlayerDeletes { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE FROM players", StringComparison.Ordinal)) PlayerDeletes++;
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!WriteExecuted && command.CommandText.StartsWith(commandPrefix, StringComparison.Ordinal))
            {
                Assert.NotNull(command.Transaction);
                WriteExecuted = true;
                await result.DisposeAsync();
                if (commandPrefix.StartsWith("UPDATE", StringComparison.Ordinal))
                {
                    // Read our own uncommitted write on the same PostgreSQL transaction
                    // before injecting failure, proving the answer actually changed.
                    var idParameter = System.Text.RegularExpressions.Regex.Match(command.CommandText, @"WHERE id = (@\w+)").Groups[1].Value;
                    Assert.False(string.IsNullOrEmpty(idParameter));
                    await using var verify = command.Connection!.CreateCommand();
                    verify.Transaction = command.Transaction;
                    var table = commandPrefix["UPDATE ".Length..];
                    verify.CommandText = $"SELECT status FROM {table} WHERE id = @id";
                    verify.Parameters.Add(new NpgsqlParameter("id", command.Parameters[idParameter].Value!));
                    ObservedStatus = (AttendanceStatus)(int)(await verify.ExecuteScalarAsync(cancellationToken))!;
                }
                if (cancellation is not null)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw new InvalidOperationException("HP84 injected failure after actual SQL write");
            }
            return result;
        }
    }
}
