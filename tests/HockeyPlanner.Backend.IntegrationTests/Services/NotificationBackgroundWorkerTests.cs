using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

public sealed class NotificationBackgroundWorkerTests
{
    [Fact]
    public async Task Disabled_DoesNotClaimOrAccessDatabase()
    {
        using var worker = Create();
        await worker.RunOnceAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_InterruptsLongPollingDelay()
    {
        using var worker = Create();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await worker.StopAsync(timeout.Token);
        Assert.False(timeout.IsCancellationRequested);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    private static NotificationBackgroundWorker Create() => new(new RejectScopes(),
        Options.Create(new NotificationWorkerOptions { Enabled = false, PollIntervalSeconds = 3600 }),
        TimeProvider.System, NullLogger<NotificationBackgroundWorker>.Instance);

    private sealed class RejectScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("Disabled worker must not access a scope.");
    }
}
