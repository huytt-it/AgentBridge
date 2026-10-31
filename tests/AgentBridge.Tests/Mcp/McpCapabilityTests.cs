using System.Diagnostics;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using AgentBridge.Mcp;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Tests.Mcp;

/// <summary>Calls through <see cref="McpCapability"/> to the stand-in server and checks how results are mapped.</summary>
public sealed class McpCapabilityTests
{
    [Fact]
    public async Task Text_content_is_returned_inline()
    {
        await using var host = new McpTestHost();
        var capability = host.Create("test.echo", Entry("echo"));

        var result = await capability.ExecuteAsync(host.Context("""{"message":"hello"}"""), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("""{"text":"hello"}""", result.Data!.ToJsonString());
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task Structured_content_becomes_the_data()
    {
        await using var host = new McpTestHost();
        var capability = host.Create("test.structured", Entry("structured"));

        var result = await capability.ExecuteAsync(host.Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("""{"items":[{"id":1,"name":"alpha"},{"id":2,"name":"beta"}],"total":2}""", result.Data!.ToJsonString());
    }

    [Fact]
    public async Task A_tool_error_becomes_an_execution_failure_with_the_tool_text()
    {
        await using var host = new McpTestHost();
        var capability = host.Create("test.fail", Entry("fail"));

        var result = await capability.ExecuteAsync(host.Context(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.ExecutionFailed, result.Error!.Code);
        Assert.Equal("Something went wrong in the tool.", result.Error.Message);
    }

    [Fact]
    public async Task A_long_tool_error_is_cut_to_2000_characters()
    {
        await using var host = new McpTestHost();
        var capability = host.Create("test.fail", Entry("fail"));

        var result = await capability.ExecuteAsync(host.Context("""{"length":5000}"""), CancellationToken.None);

        Assert.Equal(McpCapability.MaxErrorLength, result.Error!.Message.Length);
        Assert.StartsWith("Something went wrong in the tool.xxx", result.Error.Message);
        Assert.EndsWith("x...", result.Error.Message);
    }

    [Fact]
    public async Task Text_up_to_64_KB_stays_inline()
    {
        await using var host = new McpTestHost();
        var capability = host.Create("test.big", Entry("big"));
        var context = host.Context("""{"kb":64}""");

        var result = await capability.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(64 * 1024, result.Data!["text"]!.GetValue<string>().Length);
        Assert.Empty(Directory.EnumerateFiles(context.ArtifactDirectory));
    }

    [Fact]
    public async Task Larger_text_is_written_to_result_md_in_the_artifact_folder()
    {
        await using var host = new McpTestHost();
        var capability = host.Create("test.big", Entry("big"));
        var context = host.Context("""{"kb":100}""");

        var result = await capability.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.Succeeded);
        var file = Path.Combine(context.ArtifactDirectory, McpCapability.ResultFileName);
        Assert.Equal(100 * 1024, new FileInfo(file).Length);
        Assert.Equal("""{"artifact":"result.md","textBytes":102400}""", result.Data!.ToJsonString());
        Assert.Equal("The result is 100 KB of text, too large to return inline; it is published as the artifact result.md.", result.Summary);
    }

    [Fact]
    public async Task Other_content_types_are_counted_and_skipped()
    {
        await using var host = new McpTestHost();
        var capability = host.Create("test.mixed", Entry("mixed"));

        var result = await capability.ExecuteAsync(host.Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("""{"text":"first\nsecond","skippedContent":{"image":1,"resource_link":1}}""", result.Data!.ToJsonString());
        Assert.Equal("The tool also returned 2 non-text content item(s), which this agent does not forward.", result.Summary);
    }

    [Fact]
    public async Task FixedArgs_are_always_sent_and_win_over_args_from_the_request()
    {
        await using var host = new McpTestHost();
        var capability = host.Create("code.search", Entry("search_code", e =>
        {
            e.FixedArgs["root"] = "D:/fixed";
            e.FixedArgs["max_results"] = "7";
        }));

        // The validator would already reject these keys; the capability must not depend on it.
        var result = await capability.ExecuteAsync(
            host.Context("""{"query":"Invoice","root":"C:/Windows","max_results":1000}"""), CancellationToken.None);

        Assert.Equal("root=D:/fixed; query=Invoice; max_results=7", result.Data!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task TargetPath_in_FixedArgs_is_replaced_by_the_target_path()
    {
        await using var host = new McpTestHost();
        var capability = host.Create("code.search", Entry("search_code", e =>
        {
            e.RequiresTarget = true;
            e.FixedArgs["root"] = "{targetPath}";
        }));

        var result = await capability.ExecuteAsync(host.Context("""{"query":"Invoice"}""", targetPath: @"D:\repos\billing"), CancellationToken.None);

        Assert.Equal(@"root=D:\repos\billing; query=Invoice; max_results=-", result.Data!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cancellation_stops_the_call_promptly()
    {
        await using var host = new McpTestHost();
        var capability = host.Create("test.slow", Entry("slow"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            capability.ExecuteAsync(host.Context("""{"seconds":30}"""), cancellation.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {watch.Elapsed}.");
    }

    [Fact]
    public async Task A_crashed_server_is_a_failure_without_local_details_and_the_next_request_restarts_it()
    {
        await using var host = new McpTestHost();
        var crash = host.Create("test.crash", Entry("crash"));
        var echo = host.Create("test.echo", Entry("echo"));

        var crashed = await crash.ExecuteAsync(host.Context(), CancellationToken.None);
        var next = await echo.ExecuteAsync(host.Context("""{"message":"again"}"""), CancellationToken.None);

        Assert.False(crashed.Succeeded);
        Assert.Equal(ErrorCodes.ExecutionFailed, crashed.Error!.Code);
        Assert.Equal("The local tool server behind 'test.crash' stopped or could not be started, so the request did not complete. "
            + "It is started again on the next request. Try again with a new requestId; if it keeps failing, ask the user to check the agent.",
            crashed.Error.Message);
        // The reason, with the server's stderr, stays in the local log.
        Assert.Contains(host.Logs.Entries, e => e.Level == LogLevel.Error
            && e.Message.StartsWith("test.crash failed because its MCP server could not be used: MCP server 'fixture' stopped"));
        Assert.Contains(host.Logs.Entries, e => e.Category == McpServerManager.StderrCategory && e.Message == "[fixture] fixture: crashing on purpose");
        Assert.True(next.Succeeded);
        Assert.Equal("again", next.Data!["text"]!.GetValue<string>());
        Assert.Equal(2, host.StartedPids().Count);
    }

    [Fact]
    public async Task A_protocol_error_from_the_server_is_an_execution_failure()
    {
        await using var host = new McpTestHost();
        var descriptor = new CapabilityDescriptor { Name = "test.gone", Description = "A tool the server no longer has." };
        var capability = new McpCapability(descriptor, host.Manager, McpTestHost.Server, "gone", new Dictionary<string, McpFixedArg>(),
            host.Logs.CreateLogger("test"));

        var result = await capability.ExecuteAsync(host.Context(), CancellationToken.None);

        Assert.Equal(ErrorCodes.ExecutionFailed, result.Error!.Code);
        Assert.StartsWith("The tool rejected the request: ", result.Error.Message);
        Assert.Contains("Unknown tool 'gone'.", result.Error.Message);
    }

    private static CapabilityOptions Entry(string tool, Action<CapabilityOptions>? configure = null)
    {
        var entry = new CapabilityOptions { Type = "mcp", Server = McpTestHost.Server, Tool = tool, Description = $"Test capability for {tool}." };
        configure?.Invoke(entry);
        return entry;
    }
}
