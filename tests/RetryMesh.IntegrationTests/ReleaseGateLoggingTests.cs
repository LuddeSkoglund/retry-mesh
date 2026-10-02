using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RetryMesh;

namespace RetryMesh.IntegrationTests;

public class ReleaseGateLoggingTests
{
    [Fact]
    public async Task LogsAreStructuredAndSuccessDoesNotProduceInformationNoise()
    {
        var logs = new CaptureLogs();
        await using (var chain = await ReleaseGateChain.Start(logs: logs, fakeExternal: true))
        {
            using var caller = new HttpClient();
            using var response = await caller.GetAsync(chain.Url + "/execute?root=logs");
            var events = logs.Events.Where(e => e.Category.StartsWith("RetryMesh")).ToArray();
            foreach (var message in new[] { "local retries exhausted", "failure candidate recorded", "metadata propagated automatically", "Upstream retry suppressed", "untrusted dependency" })
                Assert.Contains(events, e => e.Message.Contains(message));
            Assert.Contains(events, e => e.Level == LogLevel.Information && e.Fields.ContainsKey("FailureId") && e.Fields.ContainsKey("Attempts"));
        }
        logs.Events.Clear();
        await using (var chain = await ReleaseGateChain.Start(logs: logs, recoverAt: 1))
        {
            using var caller = new HttpClient();
            using var response = await caller.GetAsync(chain.Url + "/execute?root=success");
            Assert.DoesNotContain(logs.Events, e => e.Category.StartsWith("RetryMesh") && e.Level >= LogLevel.Information);
        }
    }

    internal sealed class CaptureLogs : ILoggerProvider
    {
        internal ConcurrentQueue<Entry> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);
        public void Dispose() { }
        internal sealed record Entry(string Category, LogLevel Level, string Message, Dictionary<string, object?> Fields);
        private sealed class CaptureLogger(CaptureLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var fields = state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToDictionary(v => v.Key, v => v.Value) : [];
                owner.Events.Enqueue(new(category, level, formatter(state, exception), fields));
            }
        }
    }
}
