using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Contracts;
using Microsoft.Extensions.Options;

namespace AgentBridge.Capabilities.Native;

/// <summary>Connectivity check: proves the worker is running and the Box round trip works.</summary>
internal sealed class PingCapability : ICapability
{
    private readonly AgentBridgeOptions _options;
    private readonly TimeProvider _timeProvider;

    public PingCapability(IOptions<AgentBridgeOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public CapabilityDescriptor Descriptor { get; } = new()
    {
        Name = "agent.ping",
        Description = "Checks that the local agent is running and reachable through Box. "
            + "Returns the agent id, its version and the current time on the machine.",
        UseWhen = "Before the first request of a session, or when responses seem to stop arriving.",
        Parameters = new Dictionary<string, ParameterDescriptor>
        {
            ["echo"] = new()
            {
                Type = ParameterTypes.String,
                Description = "Optional text (up to 200 characters) that the agent returns unchanged.",
                Pattern = @"^[\s\S]{0,200}\z",
                Example = JsonValue.Create("hello"),
            },
        },
        Output = OutputFormats.Json,
        Timeout = TimeSpan.FromSeconds(10),
        ExampleArgs = new JsonObject { ["echo"] = "hello" },
    };

    public Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken)
    {
        var data = new JsonObject
        {
            ["agentId"] = _options.AgentId,
            ["agentVersion"] = AgentVersion.Current,
            ["machineTimeUtc"] = _timeProvider.GetUtcNow(),
        };

        if (context.Args.ValueKind == JsonValueKind.Object
            && context.Args.TryGetProperty("echo", out var echo)
            && echo.ValueKind == JsonValueKind.String)
        {
            data["echo"] = echo.GetString();
        }

        return Task.FromResult(CapabilityResult.Success(data, "pong"));
    }
}
