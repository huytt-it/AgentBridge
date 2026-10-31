using System.Text.Json.Nodes;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using AgentBridge.Tests.Processing.Fakes;

namespace AgentBridge.Tests.Processing;

public sealed class ArtifactPublishingTests
{
    [Fact]
    public async Task Files_in_the_artifact_folder_are_published_before_the_response()
    {
        var ping = PipelineHarness.Ping(async (context, ct) =>
        {
            await File.WriteAllTextAsync(Path.Combine(context.ArtifactDirectory, "result.md"), "# Backlog", ct);
            return CapabilityResult.Success(null, "exported");
        });
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");

        await h.ProcessInboxAsync();

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Succeeded, response.Status);
        Assert.Equal("artifacts/r1/result.md", Assert.Single(response.Artifacts).Path);
        Assert.True(h.Box.ArtifactsPublishedBeforeResponse["r1"]);
        Assert.Equal(response.Artifacts, h.Jobs.ResponseOf("r1")!.Artifacts);
    }

    [Fact]
    public async Task Artifacts_over_the_size_limit_fail_the_job_and_are_not_published()
    {
        var ping = PipelineHarness.Ping(async (context, ct) =>
        {
            await File.WriteAllBytesAsync(Path.Combine(context.ArtifactDirectory, "big.csv"), new byte[(1024 * 1024) + 1], ct);
            return CapabilityResult.Success(new JsonObject { ["rows"] = 1 }, "exported");
        });
        using var h = new PipelineHarness([ping], o => o.MaxArtifactMegabytes = 1);
        h.AddRequest("r1");

        await h.ProcessInboxAsync();

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Failed, response.Status);
        Assert.Equal(ErrorCodes.ArtifactTooLarge, response.Error!.Code);
        Assert.Contains("over this agent's limit of 1 MB", response.Error.Message);
        Assert.Null(response.Result);
        Assert.Empty(response.Artifacts);
        Assert.False(h.Box.PublishedArtifacts.ContainsKey("r1"));
    }

    [Fact]
    public async Task Failed_jobs_publish_no_artifacts()
    {
        var ping = PipelineHarness.Ping(async (context, ct) =>
        {
            await File.WriteAllTextAsync(Path.Combine(context.ArtifactDirectory, "partial.md"), "half", ct);
            return CapabilityResult.Failure(ErrorCodes.ExecutionFailed, "The tool crashed.");
        });
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");

        await h.ProcessInboxAsync();

        Assert.Equal(JobStatus.Failed, h.Box.Responses["r1"].Status);
        Assert.False(h.Box.PublishedArtifacts.ContainsKey("r1"));
    }

    [Fact]
    public async Task Jobs_that_write_nothing_list_no_artifacts()
    {
        using var h = new PipelineHarness(PipelineHarness.Ping());
        h.AddRequest("r1");

        await h.ProcessInboxAsync();

        Assert.Empty(h.Box.Responses["r1"].Artifacts);
    }

    [Fact]
    public async Task The_capability_gets_an_existing_artifact_folder_inside_its_job_folder()
    {
        var ping = PipelineHarness.Ping();
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");

        await h.ProcessInboxAsync();

        var context = ping.LastContext!;
        Assert.Equal(Path.Combine(context.JobDirectory, "artifacts"), context.ArtifactDirectory);
        Assert.True(Directory.Exists(context.ArtifactDirectory));
    }
}
