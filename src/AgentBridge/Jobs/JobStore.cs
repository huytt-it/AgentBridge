using System.Text.Json;
using AgentBridge.Contracts;

namespace AgentBridge.Jobs;

internal sealed class JobStore : IJobStore
{
    private const string RequestFileName = "request.json";
    private const string MetaFileName = "meta.json";
    private const string ResponseFileName = "response.json";
    private const string PublishedMarkerFileName = "published.marker";

    private readonly AgentBridgePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobStore> _logger;

    public JobStore(AgentBridgePaths paths, TimeProvider timeProvider, ILogger<JobStore> logger)
    {
        _paths = paths;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public void EnsureLayout() => Directory.CreateDirectory(_paths.Jobs);

    public async Task<bool> TryClaimAsync(string requestId, byte[] rawRequest, CancellationToken cancellationToken)
    {
        var directory = GetJobDirectory(requestId);
        var requestFile = Path.Combine(directory, RequestFileName);
        Directory.CreateDirectory(directory);

        // Checked before touching meta.json so a repeated claim can never disturb the first one.
        if (File.Exists(requestFile))
        {
            return false;
        }

        // Metadata first: request.json is the claim, and once it exists Find must not depend on anything missing.
        // One worker owns the data root (agentbridge.lock), so two claims never race on meta.json.
        var meta = new JobMeta(Hashing.Sha256Hex(rawRequest), _timeProvider.GetUtcNow());
        await WriteAtomicAsync(Path.Combine(directory, MetaFileName), JsonSerializer.SerializeToUtf8Bytes(meta, JsonDefaults.Options), cancellationToken);

        FileStream stream;
        try
        {
            stream = new FileStream(requestFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);
        }
        catch (IOException) when (File.Exists(requestFile))
        {
            return false;
        }

        try
        {
            await using (stream)
            {
                await stream.WriteAsync(rawRequest, cancellationToken);
            }
        }
        catch
        {
            TryDelete(requestFile); // a half-written request must not count as claimed
            throw;
        }

        _logger.LogDebug("Claimed job {RequestId}", requestId);
        return true;
    }

    public JobRecord? Find(string requestId)
    {
        var directory = GetJobDirectory(requestId);
        var requestFile = Path.Combine(directory, RequestFileName);
        if (!File.Exists(requestFile))
        {
            return null;
        }

        var meta = ReadMeta(directory);
        var sha256 = meta?.Sha256 ?? Hashing.Sha256Hex(File.ReadAllBytes(requestFile));
        var claimedAtUtc = meta?.ClaimedAtUtc ?? new DateTimeOffset(File.GetLastWriteTimeUtc(requestFile));

        return new JobRecord(
            requestId,
            directory,
            sha256,
            claimedAtUtc,
            HasResponse: File.Exists(Path.Combine(directory, ResponseFileName)),
            IsPublished: File.Exists(Path.Combine(directory, PublishedMarkerFileName)));
    }

    public async Task SaveResponseAsync(AgentResponse response, CancellationToken cancellationToken)
    {
        var directory = GetJobDirectory(response.RequestId);
        if (!File.Exists(Path.Combine(directory, RequestFileName)))
        {
            throw new InvalidOperationException($"Job '{response.RequestId}' was not claimed, so it has no response to save.");
        }

        await WriteAtomicAsync(
            Path.Combine(directory, ResponseFileName),
            JsonSerializer.SerializeToUtf8Bytes(response, JsonDefaults.Options),
            cancellationToken);
        _logger.LogDebug("Saved response for job {RequestId} ({Status})", response.RequestId, response.Status);
    }

    public async Task<AgentResponse?> ReadResponseAsync(string requestId, CancellationToken cancellationToken)
    {
        var responseFile = Path.Combine(GetJobDirectory(requestId), ResponseFileName);
        try
        {
            await using var stream = new FileStream(
                responseFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
            return await JsonSerializer.DeserializeAsync<AgentResponse>(stream, JsonDefaults.Options, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void MarkPublished(string requestId)
    {
        var directory = GetJobDirectory(requestId);
        if (!File.Exists(Path.Combine(directory, RequestFileName)))
        {
            throw new InvalidOperationException($"Job '{requestId}' was not claimed, so it has no response to mark as published.");
        }

        File.WriteAllText(Path.Combine(directory, PublishedMarkerFileName), _timeProvider.GetUtcNow().UtcDateTime.ToString("O"));
        _logger.LogDebug("Marked job {RequestId} as published", requestId);
    }

    public IReadOnlyList<JobRecord> ListUnfinished()
    {
        if (!Directory.Exists(_paths.Jobs))
        {
            return [];
        }

        var unfinished = new List<JobRecord>();
        foreach (var directory in Directory.EnumerateDirectories(_paths.Jobs))
        {
            var requestId = Path.GetFileName(directory);
            if (!Naming.IsValidRequestId(requestId))
            {
                continue; // not one of ours, e.g. a folder someone created by hand
            }

            if (Find(requestId) is { } job && (!job.HasResponse || !job.IsPublished))
            {
                unfinished.Add(job);
            }
        }

        return unfinished
            .OrderBy(job => job.ClaimedAtUtc)
            .ThenBy(job => job.RequestId, StringComparer.Ordinal)
            .ToList();
    }

    public string GetJobDirectory(string requestId)
    {
        if (!Naming.IsValidRequestId(requestId))
        {
            throw new ArgumentException($"'{requestId}' is not a valid request id.", nameof(requestId));
        }
        return Path.Combine(_paths.Jobs, requestId);
    }

    /// <summary>Null when meta.json is missing or unreadable; the caller then derives the values from request.json.</summary>
    private static JobMeta? ReadMeta(string directory)
    {
        try
        {
            var meta = JsonSerializer.Deserialize<JobMeta>(File.ReadAllBytes(Path.Combine(directory, MetaFileName)), JsonDefaults.Options);
            return string.IsNullOrEmpty(meta?.Sha256) ? null : meta;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes beside the target, then moves it into place, so readers never see half a file.</summary>
    private static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, content, cancellationToken);
            File.Move(temp, path, overwrite: true);
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

    private sealed record JobMeta(string Sha256, DateTimeOffset ClaimedAtUtc);
}
