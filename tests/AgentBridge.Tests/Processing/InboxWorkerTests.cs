using AgentBridge.Contracts;
using AgentBridge.Tests.Processing.Fakes;
using Microsoft.Extensions.Logging;
using static AgentBridge.Tests.Processing.Fakes.PipelineHarness;

namespace AgentBridge.Tests.Processing;

public sealed class InboxWorkerTests
{
    [Fact]
    public async Task Starts_up_then_polls_at_once_and_on_every_tick()
    {
        using var h = new PipelineHarness(Ping());
        h.Jobs.Seed("old", RequestJson("old"), response: null, published: false, Start.AddHours(-1));
        h.AddRequest("r1");
        using var worker = h.CreateWorker();

        await worker.StartAsync(CancellationToken.None);
        await h.Box.WaitUntilAsync(() => h.Box.Responses.ContainsKey("r1"));

        Assert.Equal(1, h.Box.EnsureLayoutCalls);
        Assert.Equal(1, h.Jobs.EnsureLayoutCalls);
        Assert.Same(h.Catalog.Current, h.Box.PublishedCatalog);
        Assert.Equal(h.Catalog.RenderReadme(), h.Box.PublishedReadme);
        Assert.Equal(JobStatus.Interrupted, h.Box.Responses["old"].Status);
        Assert.Contains(h.WorkerLogger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains(h.Paths.BoxRoot) && e.Message.Contains(FakeCatalogProvider.Version) && e.Message.Contains("1 capabilities"));

        h.AddRequest("r2");
        h.Time.Advance(TimeSpan.FromSeconds(h.Options.PollSeconds));
        await h.Box.WaitUntilAsync(() => h.Box.Responses.ContainsKey("r2"));

        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Succeeded, h.Box.Responses["r2"].Status);
    }

    [Fact]
    public async Task A_failing_pass_is_logged_and_polling_continues()
    {
        using var h = new PipelineHarness(Ping());
        h.AddRequest("r1");
        h.Box.ListInboxFailuresRemaining = 1;
        using var worker = h.CreateWorker();

        await worker.StartAsync(CancellationToken.None);
        await h.Box.WaitUntilAsync(() => h.Box.ListInboxCalls == 1);
        h.Time.Advance(TimeSpan.FromSeconds(h.Options.PollSeconds));
        await h.Box.WaitUntilAsync(() => h.Box.Responses.ContainsKey("r1"));
        await worker.StopAsync(CancellationToken.None);

        Assert.Contains(h.WorkerLogger.Entries, e => e.Level == LogLevel.Error && e.Exception is IOException);
    }

    [Fact]
    public async Task Startup_failure_stops_the_worker_with_a_clear_log()
    {
        using var h = new PipelineHarness(Ping());
        h.Box.EnsureLayoutFailure = new UnauthorizedAccessException("Access to the Box folder is denied.");
        using var worker = h.CreateWorker();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await worker.StartAsync(CancellationToken.None);
            await worker.ExecuteTask!;
        });

        var critical = Assert.Single(h.WorkerLogger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains(h.Paths.BoxRoot, critical.Message);
        Assert.Equal(0, h.Box.ListInboxCalls);
    }

    [Fact]
    public async Task Stops_cleanly_when_the_host_stops()
    {
        using var h = new PipelineHarness(Ping());
        using var worker = h.CreateWorker();

        await worker.StartAsync(CancellationToken.None);
        await h.Box.WaitUntilAsync(() => h.Box.ListInboxCalls == 1);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Contains(h.WorkerLogger.Entries, e => e.Message.Contains("stopped polling"));
        Assert.DoesNotContain(h.WorkerLogger.Entries, e => e.Level >= LogLevel.Error);
    }
}
