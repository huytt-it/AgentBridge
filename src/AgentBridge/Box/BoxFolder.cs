using System.Text;
using System.Text.Json;
using AgentBridge.Contracts;

namespace AgentBridge.Box;

internal sealed class BoxFolder : IBoxFolder
{
    private const string ReasonSuffix = ".reason.txt";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AgentBridgePaths _paths;
    private readonly ILogger<BoxFolder> _logger;
    private readonly TimeProvider _timeProvider;

    public BoxFolder(AgentBridgePaths paths, ILogger<BoxFolder> logger, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void EnsureLayout()
    {
        Directory.CreateDirectory(_paths.Inbox);
        Directory.CreateDirectory(_paths.Responses);
        Directory.CreateDirectory(_paths.Artifacts);
        Directory.CreateDirectory(_paths.Archive);
        Directory.CreateDirectory(_paths.DeadLetter);
    }

    public IReadOnlyList<InboxItem> ListInbox()
    {
        var items = new List<InboxItem>();
        try
        {
            foreach (var file in new DirectoryInfo(_paths.Inbox).EnumerateFiles())
            {
                try
                {
                    if (IsCandidate(file))
                    {
                        items.Add(new InboxItem(file.FullName, file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone or locked while listing: the next poll sees it again if it still matters.
                    _logger.LogDebug(ex, "Skipped {File} while listing the inbox", file.Name);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        return items
            .OrderBy(item => item.LastWriteTimeUtc)
            .ThenBy(item => item.FileName, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<byte[]> ReadAsync(InboxItem item, CancellationToken cancellationToken)
    {
        // ReadWrite | Delete keeps the sync client free to replace or remove the file while we read it.
        await using var stream = new FileStream(
            item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    public Task ArchiveAsync(InboxItem item, CancellationToken cancellationToken)
    {
        var moved = MoveToUniqueName(item, _paths.Archive);
        if (moved is not null)
        {
            _logger.LogDebug("Archived {File} as {Archived}", item.FileName, Path.GetFileName(moved));
        }
        return Task.CompletedTask;
    }

    public async Task DeadLetterAsync(InboxItem item, string reason, CancellationToken cancellationToken)
    {
        var moved = MoveToUniqueName(item, _paths.DeadLetter);
        if (moved is null)
        {
            return;
        }

        _logger.LogInformation("Dead-lettered {File} as {DeadLettered}: {Reason}", item.FileName, Path.GetFileName(moved), reason);

        var text = $"{_timeProvider.GetUtcNow().UtcDateTime:O}{Environment.NewLine}{reason}{Environment.NewLine}";
        try
        {
            await WriteViaStagingAsync(Utf8NoBom.GetBytes(text), moved + ReasonSuffix, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file is already out of the inbox; failing here would only make the caller look for it again.
            _logger.LogWarning(ex, "Could not write the reason file for dead-lettered {File}", item.FileName);
        }
    }

    public async Task<IReadOnlyList<ArtifactRef>> PublishArtifactsAsync(
        string requestId, string sourceDirectory, CancellationToken cancellationToken)
    {
        if (!Naming.IsValidRequestId(requestId))
        {
            throw new ArgumentException($"'{requestId}' is not a valid request id.", nameof(requestId));
        }
        if (!Directory.Exists(sourceDirectory))
        {
            return [];
        }

        var artifacts = new List<ArtifactRef>();
        var destinationRoot = Path.Combine(_paths.Artifacts, requestId);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await WriteViaStagingAsync(await File.ReadAllBytesAsync(file, cancellationToken), destination, cancellationToken);

            var boxRelative = Path.GetRelativePath(_paths.BoxRoot, destination).Replace('\\', '/');
            artifacts.Add(new ArtifactRef(boxRelative, ContentTypeOf(file), new FileInfo(destination).Length));
        }

        if (artifacts.Count > 0)
        {
            _logger.LogInformation("Published {Count} artifact(s) for {RequestId}", artifacts.Count, requestId);
        }
        return artifacts;
    }

    public async Task PublishResponseAsync(AgentResponse response, CancellationToken cancellationToken)
    {
        if (!Naming.IsValidRequestId(response.RequestId))
        {
            throw new ArgumentException($"'{response.RequestId}' is not a valid request id.", nameof(response));
        }

        var destination = Path.Combine(_paths.Responses, response.RequestId + ".json");
        await WriteViaStagingAsync(JsonSerializer.SerializeToUtf8Bytes(response, JsonDefaults.Options), destination, cancellationToken);
        _logger.LogInformation("Published response {RequestId} ({Status})", response.RequestId, response.Status);
    }

    public async Task PublishCatalogAsync(Catalog catalog, string readme, CancellationToken cancellationToken)
    {
        await WriteViaStagingAsync(JsonSerializer.SerializeToUtf8Bytes(catalog, JsonDefaults.Options), _paths.CatalogFile, cancellationToken);
        await WriteViaStagingAsync(Utf8NoBom.GetBytes(readme), _paths.ReadmeFile, cancellationToken);
        _logger.LogInformation("Published catalog {CatalogVersion}", catalog.CatalogVersion);
    }

    private static string ContentTypeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".txt" or ".log" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".xml" => "application/xml",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    // Only *.json counts, which already excludes names ending in .tmp, .part or .partial.
    private static bool IsCandidate(FileInfo file) =>
        string.Equals(file.Extension, ".json", StringComparison.OrdinalIgnoreCase)
        && !file.Name.StartsWith('.')
        && !file.Name.StartsWith('~')
        && (file.Attributes & FileAttributes.Hidden) == 0;

    /// <summary>Moves the file into the folder without ever replacing one. Null when the source is already gone.</summary>
    private string? MoveToUniqueName(InboxItem item, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        var extension = Path.GetExtension(item.FileName);

        for (var attempt = 0; ; attempt++)
        {
            var candidate = Path.Combine(destinationDirectory, attempt == 0 ? item.FileName : $"{stem}-{attempt}{extension}");
            if (File.Exists(candidate))
            {
                continue;
            }

            try
            {
                File.Move(item.FullPath, candidate, overwrite: false);
                return candidate;
            }
            catch (FileNotFoundException)
            {
                _logger.LogInformation("{File} is already gone from the inbox", item.FileName);
                return null;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Taken between the check and the move: try the next suffix.
            }
        }
    }

    /// <summary>Writes beside the Box folder (never inside it), then moves the finished file into place.</summary>
    private async Task WriteViaStagingAsync(byte[] content, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.Staging);
        var temp = Path.Combine(_paths.Staging, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, content, cancellationToken);
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
