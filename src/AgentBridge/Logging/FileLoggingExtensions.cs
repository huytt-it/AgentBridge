using Microsoft.Extensions.Options;

namespace AgentBridge.Logging;

internal static class FileLoggingExtensions
{
    /// <summary>
    /// Adds the daily log file in LocalDataRoot/logs (see <see cref="FileLoggerProvider"/>). Needs <c>AddAgentBridge</c>.
    /// Configure it like any provider: Logging:LogLevel, or Logging:File:LogLevel for this one only.
    /// </summary>
    public static IServiceCollection AddAgentBridgeFileLog(this IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(sp => new FileLoggerProvider(
            () => new FileLogSettings(
                sp.GetRequiredService<AgentBridgePaths>().Logs,
                sp.GetRequiredService<IOptions<AgentBridgeOptions>>().Value.LogRetentionDays),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
