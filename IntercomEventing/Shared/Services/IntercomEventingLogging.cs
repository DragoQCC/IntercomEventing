using Microsoft.Extensions.Logging;

namespace IntercomEventing;


internal static partial class IntercomEventingLogging
{
    [LoggerMessage(1, LogLevel.Information, "Event called")]
    internal static partial void LogEventCalled(this ILogger logger);

    [LoggerMessage(2, LogLevel.Error, "Event failed")]
    internal static partial void LogEventFailed(this ILogger logger, Exception exception);

    [LoggerMessage(3, LogLevel.Information, "Event succeeded")]
    internal static partial void LogEventSucceeded(this ILogger logger);
}