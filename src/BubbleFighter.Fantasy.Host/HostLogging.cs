using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Host;

internal static class HostLogging
{
    internal static ILoggingBuilder AddDurableFile(this ILoggingBuilder builder, RotatingFileLoggerProvider? provider)
    {
        if (provider is null) return builder;
        // A provider rule keeps file retention independent of the console threshold; category rules
        // still use the logging factory's normal provider/category precedence.
        return builder.AddProvider(provider).AddFilter<RotatingFileLoggerProvider>(null, provider.Options.Level);
    }
}
