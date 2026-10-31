using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using AgentBridge.Box;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using AgentBridge.Jobs;
using Microsoft.Extensions.Options;

namespace AgentBridge.Processing;

/// <summary>
/// The request pipeline: parse → dedup → claim → validate → execute → save → publish → archive.
/// Not meant for concurrent use: jobs run one at a time.
/// </summary>
internal sealed class RequestProcessor
{
    private const string InterruptedMessage =
        "The agent stopped while this request was running. It was not re-run because it may have had side effects. "
        + "Send a new request with a new requestId to try again.";

    private const string ResendHint = "Send a corrected request with a new requestId.";

    private readonly IBoxFolder _box;
    private readonly IJobStore _jobs;
    private readonly ICapabilityRegistry _registry;
    private readonly ICatalogProvider _catalog;
    private readonly AgentBridgeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RequestProcessor> _logger;

    public RequestProcessor(
        IBoxFolder box,
        IJobStore jobs,
        ICapabilityRegistry registry,
        ICatalogProvider catalog,
        IOptions<AgentBridgeOptions> options,
        TimeProvider timeProvider,
        ILogger<RequestProcessor> logger)
    {
        _box = box;
        _jobs = jobs;
        _registry = registry;
        _catalog = catalog;
        _options = options.Value;
        _time = timeProvider;
        _logger = logger;
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Called once at startup. Jobs claimed without a response are reported as interrupted; saved but unpublished
    /// responses are published. Nothing is ever executed again. A job that cannot be recovered is logged and skipped.
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var job in _jobs.ListUnfinished())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var saved = job.HasResponse ? await _jobs.ReadResponseAsync(job.RequestId, cancellationToken) : null;
                if (saved is null)
                {
                    await ReportInterruptedAsync(job, cancellationToken);
                }
                else if (!job.IsPublished)
                {
                    await PublishAsync(saved, cancellationToken);
                    _logger.LogInformation("Published the saved response of {RequestId} that the previous run left unpublished.", job.RequestId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Could not recover job {RequestId}; it is retried at the next start.", job.RequestId);
            }
        }
    }

    /// <summary>One pass over inbox/, one file at a time. A file that fails is logged and stays for the next pass.</summary>
    public async Task ProcessInboxAsync(CancellationToken cancellationToken)
    {
        foreach (var item in _box.ListInbox())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessItemAsync(item, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Could not process {FileName}; it stays in inbox/ and is retried on the next poll.", item.FileName);
            }
        }
    }

    /// <summary>
    /// Validates the request and runs its capability: the one execution path shared by the inbox and the run command.
    /// Never throws; every outcome, including bugs, timeouts and shutdown, becomes a response.
    /// </summary>
    /// <param name="jobDirectory">Existing local folder private to this run, passed to the capability.</param>
    /// <param name="cancellationToken">Host shutdown; when it fires the response is <see cref="JobStatus.Interrupted"/>.</param>
    public async Task<AgentResponse> ExecuteAsync(AgentRequest request, string jobDirectory, CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        var capabilityName = request.Capability;
        AgentResponse response;
        try
        {
            if (!TryValidate(request, out var capability, out var targetName, out var args, out var rejection))
            {
                capabilityName = capability?.Descriptor.Name ?? request.Capability;
                response = CreateResponse(request, capabilityName, startedAt, JobStatus.Rejected, rejection);
            }
            else
            {
                capabilityName = capability.Descriptor.Name;
                var artifactDirectory = ArtifactDirectoryOf(jobDirectory);
                Directory.CreateDirectory(artifactDirectory);
                var context = new CapabilityContext
                {
                    Request = request,
                    Args = args,
                    TargetName = targetName,
                    TargetPath = targetName is null ? null : _options.Targets[targetName].Path,
                    JobDirectory = jobDirectory,
                    ArtifactDirectory = artifactDirectory,
                };
                response = await RunCapabilityAsync(request, capability, context, startedAt, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request {RequestId} ({Capability}) failed with an unexpected error.", request.RequestId, capabilityName);
            response = CreateResponse(request, capabilityName, startedAt, JobStatus.Failed, new ErrorInfo(
                ErrorCodes.InternalError,
                "The agent hit an unexpected error while handling this request; the details are in its local log. "
                + "Try again later with a new requestId. If it keeps failing, ask the user to check the agent."));
        }

        _logger.LogInformation(
            "Request {RequestId} {Capability}: {Status} in {DurationMs} ms. Purpose: {Purpose}",
            response.RequestId,
            response.Capability,
            response.Status,
            (long)(response.CompletedAtUtc - startedAt).TotalMilliseconds,
            Shorten(request.Purpose ?? "-", 200));
        return response;
    }

    private async Task ProcessItemAsync(InboxItem item, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await _box.ReadAsync(item, cancellationToken);
        }
        catch (IOException ex)
        {
            _logger.LogDebug("Skipping {FileName} until the next poll: {Reason}", item.FileName, ex.Message);
            return;
        }

        if (!TryParse(bytes, out var request, out var parseError))
        {
            if (_time.GetUtcNow() - item.LastWriteTimeUtc < TimeSpan.FromSeconds(_options.IncompleteFileGraceSeconds))
            {
                _logger.LogDebug("Skipping {FileName}: not a complete request yet, it may still be syncing.", item.FileName);
                return;
            }
            await DeadLetterAsync(
                item,
                $"The file is not a valid request: {parseError} Write a single JSON object with schemaVersion, requestId "
                + "and capability (see README.md), then send it again.",
                cancellationToken);
            return;
        }

        if (!Naming.IsValidRequestId(request.RequestId))
        {
            await DeadLetterAsync(
                item,
                $"requestId '{Shorten(request.RequestId)}' cannot be used: it must be 1 to 128 letters, digits, '.', '_' or '-', "
                + "start and end with a letter or digit, and not be a Windows device name such as CON or NUL. "
                + "Send the request again with a valid requestId.",
                cancellationToken);
            return;
        }

        var existing = _jobs.Find(request.RequestId);
        if (existing is not null)
        {
            if (string.Equals(existing.RequestSha256, Hashing.Sha256Hex(bytes), StringComparison.OrdinalIgnoreCase))
            {
                await HandleDuplicateAsync(item, existing, cancellationToken);
            }
            else
            {
                await DeadLetterAsync(
                    item,
                    $"{ErrorCodes.DuplicateConflict}: requestId {request.RequestId} was already used for a different request; "
                    + "send it again with a new requestId.",
                    cancellationToken);
            }
            return;
        }

        if (!await _jobs.TryClaimAsync(request.RequestId, bytes, cancellationToken))
        {
            return; // claimed meanwhile; the next poll handles it as a duplicate
        }

        var jobDirectory = _jobs.GetJobDirectory(request.RequestId);
        var response = await ExecuteAsync(request, jobDirectory, cancellationToken);

        // The job has run: finish recording it even during shutdown, so it is not reported as interrupted later.
        // Artifacts reach Box before the response that lists them.
        response = await AttachArtifactsAsync(response, jobDirectory);
        await _jobs.SaveResponseAsync(response, CancellationToken.None);
        await PublishAsync(response, CancellationToken.None);
        await _box.ArchiveAsync(item, CancellationToken.None);
    }

    /// <summary>Where a job's capability writes the files to publish: {jobDirectory}/artifacts.</summary>
    public static string ArtifactDirectoryOf(string jobDirectory) => Path.Combine(jobDirectory, "artifacts");

    /// <summary>Publishes what a successful job left in its artifact folder, if it fits the size limit.</summary>
    private async Task<AgentResponse> AttachArtifactsAsync(AgentResponse response, string jobDirectory)
    {
        var directory = ArtifactDirectoryOf(jobDirectory);
        if (response.Status != JobStatus.Succeeded || !Directory.Exists(directory))
        {
            return response;
        }

        var totalBytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        if (totalBytes > (long)_options.MaxArtifactMegabytes * 1024 * 1024)
        {
            var megabytes = (totalBytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture);
            _logger.LogWarning("Request {RequestId} produced {Megabytes} MB of artifacts, over the {Limit} MB limit; nothing was published.",
                response.RequestId, megabytes, _options.MaxArtifactMegabytes);
            return response with
            {
                Status = JobStatus.Failed,
                Summary = null,
                Result = null,
                Error = new ErrorInfo(ErrorCodes.ArtifactTooLarge,
                    $"The result is {megabytes} MB, over this agent's limit of {_options.MaxArtifactMegabytes} MB, so it was not published. "
                    + "Ask for less, for example a narrower scope or a shorter period, with a new requestId."),
            };
        }

        var artifacts = await _box.PublishArtifactsAsync(response.RequestId, directory, CancellationToken.None);
        return artifacts.Count == 0 ? response : response with { Artifacts = artifacts };
    }

    /// <summary>Same request seen again, or a retry after a failure part-way: finish what is missing, never run it again.</summary>
    private async Task HandleDuplicateAsync(InboxItem item, JobRecord job, CancellationToken cancellationToken)
    {
        if (!job.IsPublished)
        {
            var saved = job.HasResponse ? await _jobs.ReadResponseAsync(job.RequestId, cancellationToken) : null;
            if (saved is null)
            {
                // Claimed but never finished: something failed after the claim. Report it like startup recovery does.
                await ReportInterruptedAsync(job, cancellationToken);
            }
            else
            {
                await PublishAsync(saved, cancellationToken);
            }
        }
        await _box.ArchiveAsync(item, cancellationToken);
        _logger.LogInformation("Request {RequestId} arrived again with identical content; archived it without running it again.", job.RequestId);
    }

    private async Task ReportInterruptedAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var request = await TryReadClaimedRequestAsync(job);
        var response = CreateResponse(
            job.RequestId,
            request?.Capability,
            request?.CorrelationId,
            job.ClaimedAtUtc,
            JobStatus.Interrupted,
            new ErrorInfo(ErrorCodes.Interrupted, InterruptedMessage));
        await _jobs.SaveResponseAsync(response, cancellationToken);
        await PublishAsync(response, cancellationToken);
        _logger.LogWarning("Request {RequestId} ({Capability}) never finished; reported it as interrupted without running it again.", job.RequestId, response.Capability);
    }

    private async Task<AgentResponse> RunCapabilityAsync(
        AgentRequest request, ICapability capability, CapabilityContext context, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var descriptor = capability.Descriptor;
        using var timeout = new CancellationTokenSource(descriptor.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        CapabilityResult? result = null;
        Task<CapabilityResult>? execution = null;
        try
        {
            execution = capability.ExecuteAsync(context, linked.Token);
            // WaitAsync also stops waiting for a capability that ignores its token.
            result = await execution.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (execution is not null && !await HasStoppedAsync(execution))
            {
                _logger.LogWarning("{Capability} did not stop when cancelled and may still be running.", descriptor.Name);
            }
        }

        if (result is { Succeeded: true })
        {
            return CreateResponse(request, descriptor.Name, startedAt, JobStatus.Succeeded, result: result);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return CreateResponse(request, descriptor.Name, startedAt, JobStatus.Interrupted,
                new ErrorInfo(ErrorCodes.Interrupted, InterruptedMessage), result);
        }
        if (timeout.IsCancellationRequested)
        {
            var seconds = (int)Math.Ceiling(descriptor.Timeout.TotalSeconds);
            return CreateResponse(request, descriptor.Name, startedAt, JobStatus.TimedOut, new ErrorInfo(
                ErrorCodes.Timeout,
                $"'{descriptor.Name}' did not finish within {seconds} seconds and was stopped. "
                + "Ask for less (for example a narrower scope) or try again later, with a new requestId."), result);
        }
        if (result is null)
        {
            throw new InvalidOperationException($"Capability '{descriptor.Name}' returned no result.");
        }

        var error = result.Error;
        return CreateResponse(request, descriptor.Name, startedAt, JobStatus.Failed, new ErrorInfo(
            string.IsNullOrWhiteSpace(error?.Code) ? ErrorCodes.ExecutionFailed : error.Code,
            string.IsNullOrWhiteSpace(error?.Message) ? $"'{descriptor.Name}' failed without giving a reason." : error.Message), result);
    }

    /// <summary>
    /// Gives a cancelled capability a moment to notice its token: its cancellation callbacks may run after ours,
    /// so checking IsCompleted at once would call every timeout unresponsive.
    /// </summary>
    private static async Task<bool> HasStoppedAsync(Task execution)
    {
        try
        {
            await execution.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            // It stopped by throwing; after a cancellation its outcome no longer matters.
        }
        return true;
    }

    private bool TryValidate(
        AgentRequest request,
        [NotNullWhen(true)] out ICapability? capability,
        out string? targetName,
        out JsonElement args,
        [NotNullWhen(false)] out ErrorInfo? rejection)
    {
        capability = null;
        targetName = null;
        args = default;
        rejection = null;

        if (request.SchemaVersion != AgentRequest.CurrentSchemaVersion)
        {
            rejection = Reject(ErrorCodes.UnsupportedSchemaVersion,
                $"schemaVersion {request.SchemaVersion} is not supported; this agent accepts schemaVersion {AgentRequest.CurrentSchemaVersion}.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.Capability))
        {
            rejection = Reject(ErrorCodes.InvalidRequest, $"capability is required. {CapabilityList()}");
            return false;
        }
        var now = _time.GetUtcNow();
        if (request.ExpiresAtUtc is { } expiresAt && expiresAt <= now)
        {
            rejection = Reject(ErrorCodes.Expired,
                $"The request expired at {FormatTime(expiresAt)} before it could run (agent time {FormatTime(now)}). "
                + "Use a later expiresAtUtc or omit it.");
            return false;
        }
        if (!_registry.TryGet(request.Capability, out capability))
        {
            rejection = Reject(ErrorCodes.UnknownCapability,
                $"Capability '{Shorten(request.Capability)}' is not available on this agent. {CapabilityList()} "
                + "Re-read catalog.json for what each one does and the args it accepts.");
            return false;
        }

        var descriptor = capability.Descriptor;
        var requestedTarget = string.IsNullOrWhiteSpace(request.Target) ? null : request.Target;
        if (descriptor.RequiresTarget)
        {
            if (requestedTarget is null)
            {
                rejection = Reject(ErrorCodes.TargetRequired, $"'{descriptor.Name}' needs a target. {TargetList()}");
                return false;
            }
            targetName = _options.Targets.Keys.FirstOrDefault(name => string.Equals(name, requestedTarget, StringComparison.OrdinalIgnoreCase));
            if (targetName is null)
            {
                rejection = Reject(ErrorCodes.UnknownTarget, $"Target '{Shorten(requestedTarget)}' is not configured on this agent. {TargetList()}");
                return false;
            }
        }
        else if (requestedTarget is not null)
        {
            rejection = Reject(ErrorCodes.InvalidRequest, $"'{descriptor.Name}' does not take a target; remove target from the request.");
            return false;
        }

        if (!ArgsValidator.TryValidate(request.Args, descriptor.Parameters, out args, out var argsError))
        {
            rejection = Reject(ErrorCodes.InvalidArgs, argsError);
            return false;
        }
        return true;
    }

    private string CapabilityList() => _registry.All.Count == 0
        ? "No capabilities are enabled on this agent."
        : $"Available capabilities: {string.Join(", ", _registry.All.Select(c => c.Descriptor.Name))}.";

    private string TargetList() => _options.Targets.Count == 0
        ? "No targets are configured on this agent; ask the user to add one."
        : $"Available targets: {string.Join(", ", _options.Targets.Keys.Order(StringComparer.OrdinalIgnoreCase))}.";

    private static ErrorInfo Reject(string code, string message) => new(code, $"{message} {ResendHint}");

    private async Task PublishAsync(AgentResponse response, CancellationToken cancellationToken)
    {
        await _box.PublishResponseAsync(response, cancellationToken);
        _jobs.MarkPublished(response.RequestId);
    }

    private async Task DeadLetterAsync(InboxItem item, string reason, CancellationToken cancellationToken)
    {
        await _box.DeadLetterAsync(item, reason, cancellationToken);
        _logger.LogWarning("Moved {FileName} to dead-letter/: {Reason}", item.FileName, reason);
    }

    private AgentResponse CreateResponse(
        AgentRequest request, string? capability, DateTimeOffset startedAt, JobStatus status,
        ErrorInfo? error = null, CapabilityResult? result = null) =>
        CreateResponse(request.RequestId, capability, request.CorrelationId, startedAt, status, error, result);

    private AgentResponse CreateResponse(
        string requestId, string? capability, string? correlationId, DateTimeOffset startedAt, JobStatus status,
        ErrorInfo? error = null, CapabilityResult? result = null) => new()
    {
        RequestId = requestId,
        Capability = string.IsNullOrEmpty(capability) ? null : capability,
        CorrelationId = correlationId,
        Status = status,
        StartedAtUtc = startedAt,
        CompletedAtUtc = _time.GetUtcNow(),
        Agent = new AgentInfo(_options.AgentId, AgentVersion.Current),
        CatalogVersion = _catalog.Current.CatalogVersion,
        Summary = result?.Summary,
        Result = result?.Data,
        Error = error,
    };

    /// <summary>Best effort: the raw request saved at claim time, for the capability and correlationId of a report.</summary>
    private async Task<AgentRequest?> TryReadClaimedRequestAsync(JobRecord job)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(job.Directory, "request.json"));
            return TryParse(bytes, out var request, out _) ? request : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the saved request of {RequestId}.", job.RequestId);
            return null;
        }
    }

    private static bool TryParse(byte[] bytes, [NotNullWhen(true)] out AgentRequest? request, out string error)
    {
        var json = bytes.AsSpan();
        if (json.StartsWith(Utf8Bom))
        {
            json = json[Utf8Bom.Length..];
        }
        try
        {
            request = JsonSerializer.Deserialize<AgentRequest>(json, JsonDefaults.Options);
            error = request is null ? "the file contains null instead of a JSON object." : "";
            return request is not null;
        }
        catch (JsonException ex)
        {
            request = null;
            error = ex.Message;
            return false;
        }
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Request values echoed in messages are cut short; requests are untrusted and may be huge.</summary>
    private static string Shorten(string value, int max = 80) => value.Length <= max ? value : value[..max] + "...";
}
