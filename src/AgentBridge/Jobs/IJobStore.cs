using AgentBridge.Contracts;

namespace AgentBridge.Jobs;

/// <summary>
/// Local, never-synced job state: one folder per request id under jobs/. The folder is the record:
/// request.json means claimed, response.json means finished, published.marker means the response reached Box.
/// </summary>
public interface IJobStore
{
    /// <summary>Creates the jobs/ folder when missing.</summary>
    void EnsureLayout();

    /// <summary>Atomically claims the id and stores the raw request bytes. False when the id was claimed before.</summary>
    Task<bool> TryClaimAsync(string requestId, byte[] rawRequest, CancellationToken cancellationToken);

    /// <summary>The job for the id, or null when it was never claimed.</summary>
    JobRecord? Find(string requestId);

    /// <summary>Saves response.json for a claimed job, replacing any earlier one.</summary>
    Task SaveResponseAsync(AgentResponse response, CancellationToken cancellationToken);

    Task<AgentResponse?> ReadResponseAsync(string requestId, CancellationToken cancellationToken);

    /// <summary>Records that the saved response was written to Box.</summary>
    void MarkPublished(string requestId);

    /// <summary>Jobs claimed but never finished (crash mid-run), or finished but not yet published.</summary>
    IReadOnlyList<JobRecord> ListUnfinished();

    /// <summary>Absolute path of the job's folder.</summary>
    string GetJobDirectory(string requestId);
}

public sealed record JobRecord(
    string RequestId,
    string Directory,
    string RequestSha256,
    DateTimeOffset ClaimedAtUtc,
    bool HasResponse,
    bool IsPublished);
