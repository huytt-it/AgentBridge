using System.Text.Json.Nodes;
using AgentBridge.Contracts;
using AgentBridge.Tests.Capabilities;
using AgentBridge.Tests.Processing.Fakes;
using static AgentBridge.Tests.Cli.CliTestSupport;

namespace AgentBridge.Tests.Cli;

/// <summary>cli entries through the real CapabilityRegistry and RequestProcessor, with Box and the job store faked.</summary>
public sealed class CliPipelineTests
{
    private const string Capability = "backlog.export";

    private static PipelineHarness CreateHarness(CapabilityOptions entry)
    {
        var registry = CapabilityTestSupport.CreateRegistry(
            CapabilityTestSupport.CreateOptions(o => o.Capabilities[Capability] = entry), [Factory()]);
        return new PipelineHarness(registry.All);
    }

    private static CapabilityOptions Backlog(int? timeoutSeconds = null)
    {
        var entry = Entry("markdown", "backlog", "--project", "{project}", "--out", "{outputFile}");
        entry.Description = "Exports the open backlog items of a project as Markdown, grouped by epic.";
        entry.UseWhen = "When you need the current scope of a project.";
        entry.Params["project"] = Param(required: true);
        entry.TimeoutSeconds = timeoutSeconds;
        return entry;
    }

    private static void Ask(PipelineHarness harness, string requestId, string capability, JsonObject args) =>
        harness.AddRequest(requestId, json =>
        {
            json["capability"] = capability;
            json["args"] = args;
        });

    [Fact]
    public async Task The_backlog_scenario_publishes_result_md_before_the_response()
    {
        using var h = CreateHarness(Backlog());
        Ask(h, "r1", Capability, new JsonObject { ["project"] = "DEMO" });

        await h.ProcessInboxAsync();

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Succeeded, response.Status);
        var artifact = Assert.Single(response.Artifacts);
        Assert.Equal("artifacts/r1/result.md", artifact.Path);
        Assert.True(h.Box.ArtifactsPublishedBeforeResponse["r1"]);
        Assert.StartsWith("Wrote result.md (", response.Summary);

        var jobDirectory = h.Jobs.GetJobDirectory("r1");
        var markdown = File.ReadAllText(Path.Combine(jobDirectory, "artifacts", "result.md"));
        Assert.Contains("# Backlog: DEMO", markdown);
        Assert.Contains("Đăng nhập và phân quyền", markdown);
        Assert.Equal(artifact.SizeBytes, new FileInfo(Path.Combine(jobDirectory, "artifacts", "result.md")).Length);
        Assert.True(File.Exists(Path.Combine(jobDirectory, "stdout.log")));
    }

    [Fact]
    public async Task Args_that_break_the_default_pattern_are_rejected_without_running_the_tool()
    {
        using var h = CreateHarness(Backlog());
        Ask(h, "r1", Capability, new JsonObject { ["project"] = "--out=somewhere-else" });

        await h.ProcessInboxAsync();

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Rejected, response.Status);
        Assert.Equal(ErrorCodes.InvalidArgs, response.Error!.Code);
        Assert.Contains("args.project must match the pattern", response.Error.Message);
        Assert.False(File.Exists(Path.Combine(h.Jobs.GetJobDirectory("r1"), "stdout.log")));
        Assert.False(h.Box.PublishedArtifacts.ContainsKey("r1"));
    }

    [Fact]
    public async Task A_request_cannot_add_args_the_entry_does_not_declare()
    {
        using var h = CreateHarness(Backlog());
        Ask(h, "r1", Capability, new JsonObject { ["project"] = "DEMO", ["exe"] = "cmd.exe", ["out"] = "C:\\secrets.txt" });

        await h.ProcessInboxAsync();

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Rejected, response.Status);
        Assert.Contains("args.exe is not an accepted arg", response.Error!.Message);
        Assert.Contains("args.out is not an accepted arg", response.Error.Message);
    }

    [Fact]
    public async Task A_failing_tool_fails_the_job_and_publishes_no_artifacts()
    {
        var entry = Entry("markdown", "fail", "4", "the project does not exist");
        entry.Description = "Always fails.";
        using var h = CreateHarness(entry);
        Ask(h, "r1", Capability, new JsonObject());

        await h.ProcessInboxAsync();

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Failed, response.Status);
        Assert.Equal(ErrorCodes.ExecutionFailed, response.Error!.Code);
        Assert.Equal("'backlog.export' exited with code 4: the project does not exist", response.Error.Message);
        Assert.False(h.Box.PublishedArtifacts.ContainsKey("r1"));
    }

    [Fact]
    public async Task A_timeout_reports_timedOut_and_kills_the_whole_process_tree()
    {
        var entry = Entry("text", "spawn-and-sleep", "120");
        entry.Description = "Sleeps.";
        entry.TimeoutSeconds = 5;
        using var h = CreateHarness(entry);
        Ask(h, "r1", Capability, new JsonObject());

        var processing = h.ProcessInboxAsync();
        var stdoutLog = Path.Combine(h.Jobs.GetJobDirectory("r1"), "stdout.log");
        var childId = await WaitForChildIdAsync(stdoutLog);
        h.Time.Advance(TimeSpan.FromSeconds(6));
        await processing.WaitAsync(TimeSpan.FromSeconds(60));

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.TimedOut, response.Status);
        Assert.Equal(ErrorCodes.Timeout, response.Error!.Code);
        Assert.True(await WaitUntilGoneAsync(childId), "the tool's child process survived the timeout");
    }

    private static async Task<int> WaitForChildIdAsync(string stdoutLog)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(stdoutLog) && int.TryParse(ReadShared(stdoutLog).Trim(), out var id))
            {
                return id;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The tool did not report the PID of its child.");
    }

    private static async Task<bool> WaitUntilGoneAsync(int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            await Task.Delay(50);
        }
        return false;
    }
}
