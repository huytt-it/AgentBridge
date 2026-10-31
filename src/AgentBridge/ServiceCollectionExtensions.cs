using AgentBridge.Box;
using AgentBridge.Capabilities;
using AgentBridge.Capabilities.Native;
using AgentBridge.Cli;
using AgentBridge.Jobs;
using AgentBridge.Mcp;
using AgentBridge.Processing;
using Microsoft.Extensions.Options;

namespace AgentBridge;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentBridge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentBridgeOptions>()
            .Bind(configuration.GetSection(AgentBridgeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AgentBridgeOptions>, AgentBridgeOptionsValidator>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
            AgentBridgePaths.ForCurrentDirectory(sp.GetRequiredService<IOptions<AgentBridgeOptions>>().Value));

        services.AddSingleton<IBoxFolder, BoxFolder>();
        services.AddSingleton<IJobStore, JobStore>();

        // Native capabilities: one line per tool.
        services.AddSingleton<ICapability, PingCapability>();

        // Config-driven capabilities: AgentBridge:Capabilities entries with Type "cli" or "mcp".
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<ICapabilityFactory, CliCapabilityFactory>();
        services.AddSingleton<McpServerManager>();
        services.AddSingleton<ICapabilityFactory, McpCapabilityFactory>();

        services.AddSingleton<ICapabilityRegistry, CapabilityRegistry>();
        services.AddSingleton<ICatalogProvider, CatalogProvider>();
        services.AddSingleton<RequestProcessor>();

        return services;
    }
}
