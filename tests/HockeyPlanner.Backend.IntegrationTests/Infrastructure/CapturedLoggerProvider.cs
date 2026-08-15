using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HockeyPlanner.Backend.IntegrationTests.Infrastructure;

public sealed class CapturedLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturedLogger(_messages, categoryName);

    public void Dispose()
    {
    }

    public async Task<IReadOnlyList<string>> WaitForAsync(
        Func<IReadOnlyList<string>, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var messages = Messages;
            if (predicate(messages))
            {
                return messages;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class CapturedLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages;
        private readonly string _categoryName;

        public CapturedLogger(ConcurrentQueue<string> messages, string categoryName)
        {
            _messages = messages;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Enqueue($"{_categoryName}: {formatter(state, exception)}");
        }
    }
}
