using System.Text.Json.Nodes;

namespace AgentBridge.Contracts;

/// <summary>
/// All statuses are final. A later version may add non-final statuses (running, awaitingApproval, ...);
/// readers must treat any status they do not know as "not finished yet".
/// </summary>
public enum JobStatus
{
    Succeeded,
    Failed,
    Rejected,
    TimedOut,
    Interrupted,
}

/// <summary>responses/{requestId}.json. Same envelope for every capability.</summary>
public sealed record AgentResponse
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string RequestId { get; init; } = "";
    public string? Capability { get; init; }
    public string? CorrelationId { get; init; }
    public JobStatus Status { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public AgentInfo Agent { get; init; } = new("", "");

    /// <summary>The catalog version this agent is running; a mismatch tells the cloud to re-read catalog.json.</summary>
    public string CatalogVersion { get; init; } = "";

    public string? Summary { get; init; }
    public JsonNode? Result { get; init; }
    public IReadOnlyList<ArtifactRef> Artifacts { get; init; } = [];

    /// <summary>Set for every status except succeeded. Written so an LLM can fix its request.</summary>
    public ErrorInfo? Error { get; init; }
}

public sealed record AgentInfo(string Id, string Version);

public sealed record ErrorInfo(string Code, string Message);

/// <summary>A file published under artifacts/; <see cref="Path"/> is relative to the Box root.</summary>
public sealed record ArtifactRef(string Path, string ContentType, long SizeBytes);
