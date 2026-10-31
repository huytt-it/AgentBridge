using System.Text.Json.Nodes;

namespace AgentBridge.Contracts;

/// <summary>
/// catalog.json: what the cloud may ask this agent for. Discovery only; every request is still validated locally.
/// </summary>
public sealed class Catalog
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Hash of the targets and capabilities; changes only when what the cloud may ask for changes.</summary>
    public string CatalogVersion { get; init; } = "";

    public DateTimeOffset GeneratedAtUtc { get; init; }
    public AgentInfo Agent { get; init; } = new("", "");
    public IReadOnlyList<CatalogTarget> Targets { get; init; } = [];
    public IReadOnlyList<CatalogCapability> Capabilities { get; init; } = [];
}

/// <summary>A target the cloud may name. The local path is never published.</summary>
public sealed record CatalogTarget(string Name, string Description);

public sealed class CatalogCapability
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? UseWhen { get; init; }
    public bool RequiresTarget { get; init; }
    public IReadOnlyDictionary<string, ParameterDescriptor> Args { get; init; } = new Dictionary<string, ParameterDescriptor>();

    /// <summary>One of <see cref="OutputFormats"/>.</summary>
    public string Output { get; init; } = OutputFormats.Json;

    public int TimeoutSeconds { get; init; }

    /// <summary>A complete request body the cloud can copy and adapt.</summary>
    public JsonObject? Example { get; init; }
}
