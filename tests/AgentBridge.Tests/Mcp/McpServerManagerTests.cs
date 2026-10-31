using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace AgentBridge.Tests.Mcp;

public sealed class McpServerManagerTests
{
    private static readonly Dictionary<string, JsonElement> NoArgs = new();

    [Fact]
    public async Task Starts_the_server_on_first_use_and_lists_its_tools()
    {
        await using var host = new McpTestHost();
        Assert.Empty(host.StartedPids());

        var tools = await host.Manager.ListToolsAsync(McpTestHost.Server, CancellationToken.None);

        Assert.Equal(["echo", "search_code", "structured", "info", "fail", "slow", "crash", "big", "mixed"], tools.Select(t => t.Name));
        Assert.Equal("Echoes the message back. Use it to test the connection.", tools[0].Description);
        Assert.Single(host.StartedPids());
    }

    [Fact]
    public async Task Reuses_one_connection_and_process_per_server()
    {
        await using var host = new McpTestHost();

        var first = await InfoAsync(host);
        await host.Manager.ListToolsAsync("FIXTURE", CancellationToken.None);
        var second = await InfoAsync(host);

        Assert.Equal(first.GetProperty("pid").GetInt32(), second.GetProperty("pid").GetInt32());
        Assert.Single(host.StartedPids());
    }

    [Fact]
    public async Task Restarts_the_server_on_the_next_use_after_it_crashed_without_retrying_the_call()
    {
        await using var host = new McpTestHost();
        var before = (await InfoAsync(host)).GetProperty("pid").GetInt32();

        var ex = await Assert.ThrowsAsync<McpServerException>(() =>
            host.Manager.CallToolAsync(McpTestHost.Server, "crash", NoArgs, CancellationToken.None));
        Assert.Equal(McpTestHost.Server, ex.Server);
        Assert.Contains("stopped or closed the connection", ex.Message);
        Assert.False(McpTestHost.IsRunning(before));
        Assert.Single(host.StartedPids()); // the crash call was not retried: no second process yet

        var after = (await InfoAsync(host)).GetProperty("pid").GetInt32();

        Assert.NotEqual(before, after);
        Assert.Equal(2, host.StartedPids().Count);
    }

    [Fact]
    public async Task A_cancelled_call_returns_promptly_and_keeps_the_connection()
    {
        await using var host = new McpTestHost();
        var pid = (await InfoAsync(host)).GetProperty("pid").GetInt32();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.Manager.CallToolAsync(McpTestHost.Server, "slow", Args("""{"seconds":30}"""), cancellation.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {watch.Elapsed}.");
        Assert.Equal(pid, (await InfoAsync(host)).GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task Passes_arguments_environment_and_working_directory()
    {
        await using var host = new McpTestHost();
        var workingDirectory = Directory.CreateDirectory(host.Root.Combine("server-cwd")).FullName;
        var server = host.Options.McpServers[McpTestHost.Server];
        server.Environment["AGENTBRIDGE_FIXTURE_MARKER"] = "marker-value";
        server.WorkingDirectory = workingDirectory;

        var info = await InfoAsync(host);
        var echo = await host.Manager.CallToolAsync(McpTestHost.Server, "echo", Args("""{"message":"hello"}"""), CancellationToken.None);

        Assert.Equal("marker-value", info.GetProperty("marker").GetString());
        Assert.Equal(Path.TrimEndingDirectorySeparator(workingDirectory), Path.TrimEndingDirectorySeparator(info.GetProperty("cwd").GetString()!), ignoreCase: true);
        Assert.Equal("hello", Assert.IsType<TextContentBlock>(Assert.Single(echo.Content)).Text);
    }

    [Fact]
    public async Task Server_stderr_goes_to_the_local_log()
    {
        await using var host = new McpTestHost();

        await host.Manager.CallToolAsync(McpTestHost.Server, "echo", Args("""{"message":"hello"}"""), CancellationToken.None);

        await WaitUntilAsync(() => host.Logs.Entries.Any(e => e.Category == McpServerManager.StderrCategory && e.Message.Contains("fixture: call echo")));
        Assert.Contains(host.Logs.Entries, e => e.Category == McpServerManager.StderrCategory && e.Message.StartsWith("[fixture] fixture pid="));
    }

    [Fact]
    public async Task Unknown_and_disabled_servers_are_refused_without_starting_anything()
    {
        await using var host = new McpTestHost(o => o.McpServers["off"] = new McpServerOptions { Command = "dotnet", Enabled = false });

        var unknown = await Assert.ThrowsAsync<McpServerException>(() => host.Manager.ListToolsAsync("nope", CancellationToken.None));
        var disabled = await Assert.ThrowsAsync<McpServerException>(() => host.Manager.ListToolsAsync("off", CancellationToken.None));

        Assert.Equal("MCP server 'nope' is not configured under AgentBridge:McpServers. Configured servers: fixture, off.", unknown.Message);
        Assert.Equal("MCP server 'off' is disabled (AgentBridge:McpServers:off:Enabled is false).", disabled.Message);
        Assert.Empty(host.StartedPids());
    }

    [Fact]
    public async Task A_server_that_exits_before_the_handshake_is_reported_with_the_reason()
    {
        await using var host = new McpTestHost();
        host.Options.McpServers["broken"] = host.FixtureServer("--fail-start");

        var ex = await Assert.ThrowsAsync<McpServerException>(() => host.Manager.ListToolsAsync("broken", CancellationToken.None));

        Assert.StartsWith($"MCP server 'broken' could not be started with '{host.Options.McpServers["broken"].Command}':", ex.Message);
        Assert.Contains("run the command by hand", ex.Message);
    }

    [Fact]
    public async Task A_server_that_never_answers_the_handshake_times_out_and_is_stopped()
    {
        await using var host = new McpTestHost();
        var server = host.FixtureServer("--hang");
        server.StartupTimeoutSeconds = 2;
        host.Options.McpServers["silent"] = server;
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<McpServerException>(() => host.Manager.ListToolsAsync("silent", CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Startup timeout took {watch.Elapsed}.");
        Assert.Contains("did not complete the MCP handshake within 2 seconds", ex.Message);
        Assert.Contains("AgentBridge:McpServers:silent:StartupTimeoutSeconds", ex.Message);
        Assert.All(host.StartedPids(), pid => Assert.False(McpTestHost.IsRunning(pid)));
    }

    [Fact]
    public async Task A_command_that_does_not_exist_is_reported()
    {
        await using var host = new McpTestHost(o => o.McpServers["missing"] = new McpServerOptions { Command = "agentbridge-no-such-command-xyz", StartupTimeoutSeconds = 10 });

        var ex = await Assert.ThrowsAsync<McpServerException>(() => host.Manager.ListToolsAsync("missing", CancellationToken.None));

        Assert.Contains("MCP server 'missing'", ex.Message);
        Assert.Contains("agentbridge-no-such-command-xyz", ex.Message);
    }

    [Fact]
    public async Task Dispose_stops_every_server_process()
    {
        var host = new McpTestHost();
        host.Options.McpServers["second"] = host.FixtureServer();
        var first = (await InfoAsync(host)).GetProperty("pid").GetInt32();
        await host.Manager.ListToolsAsync("second", CancellationToken.None);
        Assert.Equal(2, host.StartedPids().Count);

        await host.Manager.DisposeAsync();

        Assert.All(host.StartedPids(), pid => Assert.False(McpTestHost.IsRunning(pid)));
        Assert.False(McpTestHost.IsRunning(first));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.Manager.ListToolsAsync(McpTestHost.Server, CancellationToken.None));
        await host.DisposeAsync();
    }

    private static async Task<JsonElement> InfoAsync(McpTestHost host)
    {
        var result = await host.Manager.CallToolAsync(McpTestHost.Server, "info", NoArgs, CancellationToken.None);
        return result.StructuredContent!.Value;
    }

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }
        Assert.True(condition());
    }
}
