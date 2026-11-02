using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Contracts;

namespace AgentBridge.Capabilities;

/// <summary>
/// One thing the cloud can ask for. Native tools implement this directly; CLI and MCP tools will be
/// config-driven implementations of it. The pipeline only ever sees this interface.
/// </summary>
public interface ICapability
{
    CapabilityDescriptor Descriptor { get; }

    /// <summary>
    /// Runs the capability. Args are already validated against <see cref="CapabilityDescriptor.Parameters"/>.
    /// The token fires on timeout and on shutdown: stop promptly when it does. The pipeline then stops waiting and
    /// reports the job, but it cannot stop work the capability keeps doing.
    /// Return a failure result for expected errors; throw only for bugs.
    /// </summary>
    Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken);
}

public sealed record CapabilityDescriptor
{
    /// <summary>Lowercase dotted name, see <see cref="Naming.IsValidCapabilityName"/>.</summary>
    public required string Name { get; init; }

    /// <summary>What the capability returns, in business terms. Published in catalog.json.</summary>
    public required string Description { get; init; }

    /// <summary>When the cloud should choose this capability.</summary>
    public string? UseWhen { get; init; }

    /// <summary>Questions a business user might ask that this capability answers. Published in catalog.json.</summary>
    public IReadOnlyList<string> ExampleQuestions { get; init; } = [];

    /// <summary>
    /// One of <see cref="AccessLevels"/>. Only <see cref="AccessLevels.Read"/> can be enabled until a local approval step
    /// exists. The registry sets the effective value from configuration.
    /// </summary>
    public string Access { get; init; } = AccessLevels.Read;

    public bool RequiresTarget { get; init; }

    public IReadOnlyDictionary<string, ParameterDescriptor> Parameters { get; init; } = new Dictionary<string, ParameterDescriptor>();

    /// <summary>One of <see cref="OutputFormats"/>.</summary>
    public string Output { get; init; } = OutputFormats.Json;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Example args for catalog.json.</summary>
    public JsonObject? ExampleArgs { get; init; }
}

public sealed class CapabilityContext
{
    public required AgentRequest Request { get; init; }

    /// <summary>Validated args; an empty object when the request had none.</summary>
    public required JsonElement Args { get; init; }

    public string? TargetName { get; init; }

    /// <summary>Absolute local path resolved from the target allowlist.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Local, never-synced folder private to this job, for logs and scratch files.</summary>
    public required string JobDirectory { get; init; }

    /// <summary>
    /// Local folder, empty at start. When the job succeeds, every file written here is published to Box under
    /// artifacts/{requestId}/ before the response; the response lists them. Put large results here, not in Data.
    /// </summary>
    public required string ArtifactDirectory { get; init; }
}

public sealed class CapabilityResult
{
    public bool Succeeded { get; init; }
    public string? Summary { get; init; }
    public JsonNode? Data { get; init; }
    public ErrorInfo? Error { get; init; }

    public static CapabilityResult Success(JsonNode? data, string? summary = null) =>
        new() { Succeeded = true, Data = data, Summary = summary };

    public static CapabilityResult Failure(string code, string message, JsonNode? data = null) =>
        new() { Succeeded = false, Error = new ErrorInfo(code, message), Data = data };
}
