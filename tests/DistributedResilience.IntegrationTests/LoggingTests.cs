using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace DistributedResilience.IntegrationTests;

public class LoggingTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("C")]
    public async Task SamplesCanLogFailuresWithoutWindowsEventLogPermissions(string service)
    {
        await using var app = service switch
        {
            "A" => ServiceAHost.Build([]),
            "B" => ServiceBHost.Build([]),
            _ => ServiceCHost.Build([])
        };

        // Windows defaults include EventLog, which may throw on Polly's warning/error logs.
        var providers = app.Services.GetServices<ILoggerProvider>().ToArray();
        Assert.DoesNotContain(providers, provider => provider.GetType().Name == "EventLogLoggerProvider");
        Assert.Contains(providers, provider => provider is ConsoleLoggerProvider);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Polly");
        logger.LogWarning("Retry attempt failed");
        logger.LogError("Retry policy exhausted");
    }
}
