using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using Microsoft.Extensions.Options;

namespace AgentBridge.Tests.Capabilities;

/// <summary>A capability with a fixed descriptor that counts how often it ran.</summary>
internal sealed class FakeCapability : ICapability
{
    public FakeCapability(string name = "test.fake")
        : this(new CapabilityDescriptor { Name = name, Description = $"Description of {name}." })
    {
    }

    public FakeCapability(CapabilityDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public CapabilityDescriptor Descriptor { get; }

    public int ExecuteCount { get; private set; }

    public Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken)
    {
        ExecuteCount++;
        return Task.FromResult(CapabilityResult.Success(null, "ran"));
    }
}

internal static class CapabilityTestSupport
{
    public static IOptions<AgentBridgeOptions> CreateOptions(Action<AgentBridgeOptions>? configure = null)
    {
        var options = new AgentBridgeOptions { AgentId = "test-agent" };
        configure?.Invoke(options);
        return Options.Create(options);
    }

    public static CapabilityRegistry CreateRegistry(IOptions<AgentBridgeOptions> options, params ICapability[] capabilities) =>
        new(capabilities, [], options);

    public static CapabilityRegistry CreateRegistry(
        IOptions<AgentBridgeOptions> options, IEnumerable<ICapabilityFactory> factories, params ICapability[] capabilities) =>
        new(capabilities, factories, options);

    public static CapabilityRegistry CreateRegistry(params ICapability[] capabilities) =>
        CreateRegistry(CreateOptions(), capabilities);

    public static CapabilityContext CreateContext(string argsJson = "{}")
    {
        using var document = System.Text.Json.JsonDocument.Parse(argsJson);
        return new CapabilityContext
        {
            Request = new AgentRequest { SchemaVersion = 1, RequestId = "r1", Capability = "test.fake" },
            Args = document.RootElement.Clone(),
            JobDirectory = System.IO.Path.GetTempPath(),
            ArtifactDirectory = System.IO.Path.GetTempPath(),
        };
    }
}

/// <summary>A factory for Type "fake" that turns entries into <see cref="FakeCapability"/>, or reports an error.</summary>
internal sealed class FakeCapabilityFactory : ICapabilityFactory
{
    public string Type => "fake";

    public List<string> Created { get; } = [];

    public string? ErrorToReport { get; set; }

    public ICapability? Create(string name, CapabilityOptions options, IList<string> errors)
    {
        if (ErrorToReport is not null)
        {
            errors.Add($"AgentBridge:Capabilities:{name}:Exe {ErrorToReport}");
            return null;
        }
        Created.Add(name);
        return new FakeCapability(new CapabilityDescriptor { Name = name, Description = options.Description ?? "" });
    }
}
