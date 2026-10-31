using System.Text.Json;

namespace AgentBridge.Contracts;

/// <summary>
/// A request file dropped into inbox/. Unknown JSON fields are ignored, so later versions can add fields
/// without breaking older agents.
/// </summary>
public sealed class AgentRequest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; }

    /// <summary>Unique per request; becomes a file and folder name, see <see cref="Naming.IsValidRequestId"/>.</summary>
    public string RequestId { get; init; } = "";

    public string Capability { get; init; } = "";

    /// <summary>Name of a locally configured target. Never a path.</summary>
    public string? Target { get; init; }

    /// <summary>Capability arguments. Must be a JSON object when present.</summary>
    public JsonElement? Args { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>Why the cloud wants this. Logged only; never used to decide what runs.</summary>
    public string? Purpose { get; init; }

    /// <summary>Groups requests that belong to one analysis.</summary>
    public string? CorrelationId { get; init; }
}
