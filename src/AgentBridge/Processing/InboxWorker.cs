using AgentBridge.Box;
using AgentBridge.Capabilities;
using AgentBridge.Jobs;
using Microsoft.Extensions.Options;

namespace AgentBridge.Processing;

/// <summary>Publishes the catalog, recovers unfinished jobs, then polls the inbox until the host stops.</summary>
internal sealed class InboxWorker : BackgroundService
{
    private readonly RequestProcessor _processor;
    private readonly IBoxFolder _box;
    private readonly IJobStore _jobs;
    private readonly ICatalogProvider _catalog;
    private readonly AgentBridgePaths _paths;
    private readonly AgentBridgeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<InboxWorker> _logger;

    public InboxWorker(
        RequestProcessor processor,
        IBoxFolder box,
        IJobStore jobs,
        ICatalogProvider catalog,
        AgentBridgePaths paths,
        IOptions<AgentBridgeOptions> options,
        TimeProvider timeProvider,
        ILogger<InboxWorker> logger)
    {
        _processor = processor;
        _box = box;
        _jobs = jobs;
        _catalog = catalog;
        _paths = paths;
        _options = options.Value;
        _time = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await StartUpAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "AgentBridge could not start. Check that BoxRoot ({BoxRoot}) and LocalDataRoot ({LocalDataRoot}) are reachable and writable.",
                _paths.BoxRoot,
                _paths.LocalDataRoot);
            throw;
        }

        // Created before the first pass so a tick that falls during a long pass is not lost.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollSeconds), _time);
        try
        {
            do
            {
                await PollOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        _logger.LogInformation("AgentBridge stopped polling.");
    }

    private async Task StartUpAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Log files: {LogFolder} (agentbridge-yyyyMMdd.log, one per day, files older than {RetentionDays} days are deleted).",
            _paths.Logs,
            _options.LogRetentionDays);
        _box.EnsureLayout();
        _jobs.EnsureLayout();
        var catalog = _catalog.Current;
        await _box.PublishCatalogAsync(catalog, _catalog.RenderReadme(), cancellationToken);
        await _processor.RecoverAsync(cancellationToken);
        _logger.LogInformation(
            "AgentBridge {Version} is watching {BoxRoot}: catalog {CatalogVersion}, {CapabilityCount} capabilities, polling every {PollSeconds}s.",
            AgentVersion.Current,
            _paths.BoxRoot,
            catalog.CatalogVersion,
            catalog.Capabilities.Count,
            _options.PollSeconds);
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _processor.ProcessInboxAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Inbox pass failed; retrying in {PollSeconds}s.", _options.PollSeconds);
        }
    }
}
