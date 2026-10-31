using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using AgentBridge.Processing;
using AgentBridge.Tests.Processing.Fakes;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AgentBridge.Tests.Mcp;

/// <summary>Type "mcp" entries through the real CapabilityRegistry and RequestProcessor, with the system clock.</summary>
public sealed class McpPipelineTests
{
    [Fact]
    public async Task A_request_runs_the_approved_tool_with_validated_args_and_the_target_path()
    {
        using var h = new PipelineHarness([]);
        await using var host = Configure(h, out var processor, out var repoPath);

        var response = await processor.ExecuteAsync(Request("r1", "code.search", """{"query":"Invoice","max_results":5.0}""", "REPO"), JobDirectory(h, "r1"), CancellationToken.None);

        Assert.Equal(JobStatus.Succeeded, response.Status);
        Assert.Equal($"root={repoPath}; query=Invoice; max_results=5", response.Result!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_cloud_cannot_send_a_fixed_arg()
    {
        using var h = new PipelineHarness([]);
        await using var host = Configure(h, out var processor, out _);

        var response = await processor.ExecuteAsync(Request("r1", "code.search", """{"query":"Invoice","root":"C:/Windows"}""", "repo"), JobDirectory(h, "r1"), CancellationToken.None);

        Assert.Equal(JobStatus.Rejected, response.Status);
        Assert.Equal(ErrorCodes.InvalidArgs, response.Error!.Code);
        Assert.StartsWith("Invalid args: args.root is not an accepted arg. Accepted args: max_results (integer, optional), query (string, required).", response.Error.Message);
        Assert.Single(host.StartedPids()); // started once by the registry, never called
    }

    [Fact]
    public async Task A_call_that_runs_past_the_capability_timeout_is_cancelled_and_reported_as_timed_out()
    {
        using var h = new PipelineHarness([]);
        await using var host = Configure(h, out var processor, out _);
        var watch = Stopwatch.StartNew();

        var response = await processor.ExecuteAsync(Request("r1", "test.slow", """{"seconds":30}"""), JobDirectory(h, "r1"), CancellationToken.None);

        Assert.Equal(JobStatus.TimedOut, response.Status);
        Assert.Equal(ErrorCodes.Timeout, response.Error!.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The timeout took {watch.Elapsed}.");

        // The cancelled call released the server: the next request does not wait behind it.
        watch.Restart();
        var next = await processor.ExecuteAsync(Request("r2", "code.search", """{"query":"Invoice"}""", "repo"), JobDirectory(h, "r2"), CancellationToken.None);
        Assert.Equal(JobStatus.Succeeded, next.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The next request took {watch.Elapsed}.");
        Assert.Single(host.StartedPids());
    }

    [Fact]
    public async Task A_large_text_result_is_published_as_an_artifact()
    {
        using var h = new PipelineHarness([]);
        await using var host = Configure(h, out var processor, out _);
        h.Box.AddRequest("r1.json", PipelineHarness.RequestJson("r1", "test.big", json => json["args"] = new System.Text.Json.Nodes.JsonObject { ["kb"] = 100 }), h.Time.GetUtcNow());

        await processor.ProcessInboxAsync(CancellationToken.None);

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Succeeded, response.Status);
        Assert.Equal("artifacts/r1/result.md", Assert.Single(response.Artifacts).Path);
        Assert.Equal(100 * 1024, response.Artifacts[0].SizeBytes);
        Assert.Equal("result.md", response.Result!["artifact"]!.GetValue<string>());
    }

    private static McpTestHost Configure(PipelineHarness h, out RequestProcessor processor, out string repoPath)
    {
        repoPath = Directory.CreateDirectory(h.Root.Combine("repo")).FullName;
        var path = repoPath;
        var host = new McpTestHost(o =>
        {
            o.Targets["repo"] = new TargetOptions { Path = path, Description = "The billing application." };
            o.Capabilities["code.search"] = new CapabilityOptions
            {
                Type = "mcp",
                Server = McpTestHost.Server,
                Tool = "search_code",
                Description = "Finds where a business term appears in the code of an application.",
                RequiresTarget = true,
                FixedArgs = { ["root"] = "{targetPath}" },
            };
            o.Capabilities["test.slow"] = new CapabilityOptions
            {
                Type = "mcp", Server = McpTestHost.Server, Tool = "slow", Description = "Waits.", TimeoutSeconds = 1,
            };
            o.Capabilities["test.big"] = new CapabilityOptions
            {
                Type = "mcp", Server = McpTestHost.Server, Tool = "big", Description = "Returns a large text.",
            };
        }, h.Options);

        var options = MsOptions.Create(h.Options);
        var registry = new CapabilityRegistry([], [host.Factory], options);
        processor = new RequestProcessor(h.Box, h.Jobs, registry, h.Catalog, options, TimeProvider.System, h.Logger);
        return host;
    }

    private static AgentRequest Request(string requestId, string capability, string argsJson, string? target = null) => new()
    {
        SchemaVersion = AgentRequest.CurrentSchemaVersion,
        RequestId = requestId,
        Capability = capability,
        Target = target,
        Args = JsonSerializer.Deserialize<JsonElement>(argsJson),
    };

    private static string JobDirectory(PipelineHarness h, string requestId) =>
        Directory.CreateDirectory(h.Root.Combine("jobs", requestId)).FullName;
}
