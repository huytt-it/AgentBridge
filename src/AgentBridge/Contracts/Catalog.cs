using System.Text.Json.Nodes;

namespace AgentBridge.Contracts;

/// <summary>
/// catalog.json: what the cloud may ask this agent for. Discovery only; every request is still validated locally.
/// </summary>
public sealed class Catalog
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Hash of the targets, domains, playbooks and capabilities; changes only when what the cloud may ask for changes.</summary>
    public string CatalogVersion { get; init; } = "";

    public DateTimeOffset GeneratedAtUtc { get; init; }
    public AgentInfo Agent { get; init; } = new("", "");
    public IReadOnlyList<CatalogTarget> Targets { get; init; } = [];

    /// <summary>Business areas that have enabled capabilities, ordered by name.</summary>
    public IReadOnlyList<CatalogDomain> Domains { get; init; } = [];

    /// <summary>Multi-step business goals, ordered by name. Guidance only: the cloud sends one request per step.</summary>
    public IReadOnlyList<CatalogPlaybook> Playbooks { get; init; } = [];

    public IReadOnlyList<CatalogCapability> Capabilities { get; init; } = [];
}

/// <summary>A target the cloud may name. The local path is never published.</summary>
public sealed record CatalogTarget(string Name, string Description);

/// <summary>A business area: every capability whose name starts with "{name}." belongs to it.</summary>
public sealed class CatalogDomain
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? UseWhen { get; init; }

    /// <summary>Questions a business user might ask that this domain answers.</summary>
    public IReadOnlyList<string> ExampleQuestions { get; init; } = [];

    /// <summary>Names of the domain's enabled capabilities, ordered.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

/// <summary>A business goal reached by calling capabilities in order. Nothing runs it locally; the cloud follows the steps.</summary>
public sealed class CatalogPlaybook
{
    public string Name { get; init; } = "";
    public string Goal { get; init; } = "";
    public string? UseWhen { get; init; }
    public IReadOnlyList<string> ExampleQuestions { get; init; } = [];

    /// <summary>In the order they are meant to run.</summary>
    public IReadOnlyList<CatalogPlaybookStep> Steps { get; init; } = [];
}

public sealed record CatalogPlaybookStep(string Capability, string Purpose);

public sealed class CatalogCapability
{
    public string Name { get; init; } = "";

    /// <summary>The first segment of the name; see <see cref="Catalog.Domains"/>.</summary>
    public string Domain { get; init; } = "";

    public string Description { get; init; } = "";
    public string? UseWhen { get; init; }
    public IReadOnlyList<string> ExampleQuestions { get; init; } = [];

    /// <summary>One of <see cref="AccessLevels"/>.</summary>
    public string Access { get; init; } = AccessLevels.Read;

    public bool RequiresTarget { get; init; }
    public IReadOnlyDictionary<string, ParameterDescriptor> Args { get; init; } = new Dictionary<string, ParameterDescriptor>();

    /// <summary>One of <see cref="OutputFormats"/>.</summary>
    public string Output { get; init; } = OutputFormats.Json;

    public int TimeoutSeconds { get; init; }

    /// <summary>A complete request body the cloud can copy and adapt.</summary>
    public JsonObject? Example { get; init; }
}

/// <summary>Values of the "access" field: what a capability may do on the machine.</summary>
public static class AccessLevels
{
    /// <summary>Reads data and changes nothing. The only level this version can enable.</summary>
    public const string Read = "read";

    /// <summary>Changes something. Reserved: enabling it needs a local approval step, which does not exist yet.</summary>
    public const string Write = "write";
}
