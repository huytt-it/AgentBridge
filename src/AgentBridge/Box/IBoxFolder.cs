using AgentBridge.Contracts;

namespace AgentBridge.Box;

/// <summary>
/// The Box-synced folder as seen from this machine. Transport only: no validation, no business rules.
/// Must tolerate sync delays, partially written files and files that disappear mid-operation.
/// </summary>
public interface IBoxFolder
{
    /// <summary>Creates inbox/, responses/, artifacts/, archive/ and dead-letter/ when missing.</summary>
    void EnsureLayout();

    /// <summary>
    /// Candidate request files in inbox/, oldest first. Only *.json; skips hidden and temporary files
    /// (names starting with '.' or '~', or ending in .tmp, .part, .partial).
    /// </summary>
    IReadOnlyList<InboxItem> ListInbox();

    /// <summary>Reads the whole file. Throws IOException when it is locked or already gone.</summary>
    Task<byte[]> ReadAsync(InboxItem item, CancellationToken cancellationToken);

    /// <summary>Moves a handled request to archive/. Never overwrites: adds a suffix when the name is taken.</summary>
    Task ArchiveAsync(InboxItem item, CancellationToken cancellationToken);

    /// <summary>Moves an unusable file to dead-letter/ and writes the reason to {fileName}.reason.txt beside it.</summary>
    Task DeadLetterAsync(InboxItem item, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Copies every file under <paramref name="sourceDirectory"/> (keeping subfolders) to artifacts/{requestId}/,
    /// replacing earlier copies, and returns them with paths relative to the Box root. Empty when there are none.
    /// </summary>
    Task<IReadOnlyList<ArtifactRef>> PublishArtifactsAsync(string requestId, string sourceDirectory, CancellationToken cancellationToken);

    /// <summary>Writes responses/{requestId}.json, replacing any earlier copy, so publishing again is safe.</summary>
    Task PublishResponseAsync(AgentResponse response, CancellationToken cancellationToken);

    /// <summary>Writes catalog.json and README.md at the Box root.</summary>
    Task PublishCatalogAsync(Catalog catalog, string readme, CancellationToken cancellationToken);
}

public sealed record InboxItem(string FullPath, string FileName, long Length, DateTimeOffset LastWriteTimeUtc);
