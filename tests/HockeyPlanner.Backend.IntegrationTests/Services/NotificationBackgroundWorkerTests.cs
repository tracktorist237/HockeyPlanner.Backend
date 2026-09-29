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
        var clock = new SignalingTimeProvider();
        using var worker = Create(clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await worker.StartAsync(timeout.Token);
        await clock.TimerCreated.WaitAsync(timeout.Token);
        await worker.StopAsync(timeout.Token);
        await worker.ExecuteTask!.WaitAsync(timeout.Token);
        Assert.False(timeout.IsCancellationRequested);
        Assert.True(worker.ExecuteTask.IsCompleted);
        Assert.False(worker.ExecuteTask.IsFaulted);
        Assert.False(worker.ExecuteTask.IsCanceled);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    private static NotificationBackgroundWorker Create(TimeProvider? clock = null) => new(new RejectScopes(),
        Options.Create(new NotificationWorkerOptions { Enabled = false, PollIntervalSeconds = 3600 }),
        clock ?? TimeProvider.System, NullLogger<NotificationBackgroundWorker>.Instance);

    private sealed class SignalingTimeProvider : TimeProvider
    {
        private readonly TaskCompletionSource _timerCreated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TimerCreated => _timerCreated.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = TimeProvider.System.CreateTimer(callback, state, dueTime, period);
            _timerCreated.TrySetResult();
            return timer;
        }
    }

    private sealed class RejectScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("Disabled worker must not access a scope.");
    }
}
