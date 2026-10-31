using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AgentBridge.Tests.Mcp;

public sealed class McpCommandsTests
{
    [Fact]
    public async Task Text_output_lists_each_tool_with_description_schema_and_a_capability_entry()
    {
        await using var host = new McpTestHost();

        var (exitCode, output) = await RunAsync(host);

        Assert.Equal(0, exitCode);
        Assert.Contains("MCP server 'fixture': 9 tool(s).", output);
        Assert.Contains("untrusted text written for coding agents", output);
        Assert.Contains("  Tool: search_code", output);
        Assert.Contains("    Description: Searches source files under root for the query.", output);
        Assert.Contains("    Annotations: none (the server does not say whether the tool changes anything)", output);
        Assert.Contains("    Annotations: readOnlyHint=true, destructiveHint=false (hints from the server, not guarantees)", output);
        Assert.Contains("""        "max_results": {""", output);
        Assert.Contains("""      "fixture.search-code": {""", output);
        Assert.Contains("""        "Type": "mcp",""", output);
        Assert.Contains("""        "Tool": "search_code",""", output);
        Assert.Contains("""        "Description": "REPLACE: what this returns, in business terms (published to the cloud)",""", output);
        Assert.Contains("  Tool: mixed", output);
        Assert.Contains("    Description: (none)", output);
    }

    [Fact]
    public async Task Json_output_is_machine_readable()
    {
        await using var host = new McpTestHost();

        var (exitCode, output) = await RunAsync(host, "--json");

        Assert.Equal(0, exitCode);
        var server = Assert.Single(JsonNode.Parse(output)!["servers"]!.AsArray())!;
        Assert.Equal("fixture", (string)server["name"]!);
        Assert.Null(server["error"]);
        var tools = server["tools"]!.AsArray();
        Assert.Equal(9, tools.Count);
        var search = tools.Single(t => (string)t!["name"]! == "search_code")!;
        Assert.Equal("""["query","root"]""", search["inputSchema"]!["required"]!.ToJsonString());
        Assert.Equal("fixture.search-code", (string)search["capabilityName"]!);
        var entry = search["capabilityEntry"]!.AsObject();
        Assert.Equal("mcp", (string)entry["Type"]!);
        Assert.Equal("fixture", (string)entry["Server"]!);
        Assert.Equal(["query", "max_results", "root"], entry["Params"]!.AsObject().Select(p => p.Key));
        Assert.Equal("Text to search for.", (string)entry["Params"]!["query"]!["Description"]!);
        Assert.Null(search["annotations"]);
        var echo = tools.Single(t => (string)t!["name"]! == "echo")!;
        Assert.Equal("""{"readOnlyHint":true,"destructiveHint":false}""", echo["annotations"]!.ToJsonString());
    }

    [Fact]
    public async Task The_suggested_entry_binds_to_a_working_capability_once_the_description_is_written()
    {
        await using var host = new McpTestHost();
        var (_, output) = await RunAsync(host, "fixture", "--json");
        var echo = JsonNode.Parse(output)!["servers"]![0]!["tools"]!.AsArray().Single(t => (string)t!["name"]! == "echo")!;
        var entry = echo["capabilityEntry"]!.DeepClone().AsObject();
        entry["Description"] = "Sends a message back, to check that the tool server works.";
        entry.Remove("UseWhen");

        var json = new JsonObject { ["AgentBridge"] = new JsonObject { ["Capabilities"] = new JsonObject { ["fixture.echo"] = entry } } };
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()))).Build();
        var options = configuration.GetSection("AgentBridge:Capabilities:fixture.echo").Get<CapabilityOptions>()!;
        var capability = host.Create("fixture.echo", options);
        var result = await capability.ExecuteAsync(host.Context("""{"message":"pasted"}"""), CancellationToken.None);

        Assert.Equal("pasted", result.Data!["text"]!.GetValue<string>());
        Assert.Equal(TimeSpan.FromSeconds(60), capability.Descriptor.Timeout);
    }

    [Fact]
    public async Task A_server_that_fails_to_start_is_reported_with_exit_code_1_and_the_others_still_listed()
    {
        await using var host = new McpTestHost();
        host.Options.McpServers["broken"] = host.FixtureServer("--fail-start");

        var (exitCode, output) = await RunAsync(host);
        var (jsonExitCode, json) = await RunAsync(host, "--json");

        Assert.Equal(1, exitCode);
        Assert.Contains("MCP server 'broken': FAILED", output);
        Assert.Contains("  MCP server 'broken' could not be started with", output);
        Assert.Contains("MCP server 'fixture': 9 tool(s).", output);
        Assert.Equal(1, jsonExitCode);
        var broken = JsonNode.Parse(json)!["servers"]![0]!;
        Assert.Equal("broken", (string)broken["name"]!);
        Assert.Contains("exit code: 3", (string)broken["error"]!);
        Assert.Null(broken["tools"]);
    }

    [Fact]
    public async Task Only_the_named_server_is_started()
    {
        await using var host = new McpTestHost();
        host.Options.McpServers["broken"] = host.FixtureServer("--fail-start");

        var (exitCode, output) = await RunAsync(host, "FIXTURE");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("broken", output);
        Assert.Single(host.StartedPids());
    }

    [Fact]
    public async Task Disabled_servers_are_not_started_unless_named()
    {
        await using var host = new McpTestHost(o => o.McpServers[McpTestHost.Server].Enabled = false);

        var (exitCode, output) = await RunAsync(host);
        var (namedExitCode, namedOutput) = await RunAsync(host, "fixture");

        Assert.Equal(1, exitCode);
        Assert.StartsWith("No enabled MCP servers are configured.", output);
        Assert.Equal(1, namedExitCode);
        Assert.Contains("MCP server 'fixture' is disabled", namedOutput);
        Assert.Empty(host.StartedPids());
    }

    [Theory]
    [InlineData(new[] { "nope" }, "MCP server 'nope' is not configured under AgentBridge:McpServers. Configured servers: fixture.")]
    [InlineData(new[] { "--verbose" }, "Unknown option '--verbose'.")]
    [InlineData(new[] { "fixture", "other" }, "Unknown option 'other'.")]
    public async Task Usage_errors_exit_with_2(string[] args, string expected)
    {
        await using var host = new McpTestHost();

        var (exitCode, output) = await RunAsync(host, args);

        Assert.Equal(2, exitCode);
        Assert.StartsWith(expected, output);
        Assert.Empty(host.StartedPids());
    }

    [Theory]
    [InlineData("code-map", "search_code", "code-map.search-code")]
    [InlineData("Backlog_Exporter", "exportItems", "backlog-exporter.exportitems")]
    [InlineData("srv", "2fa.check", "srv.t-2fa-check")]
    public void Suggested_names_are_valid_capability_names(string server, string tool, string expected)
    {
        Assert.Equal(expected, McpCommands.SuggestName(server, tool));
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(McpTestHost host, params string[] args)
    {
        var services = new ServiceCollection()
            .AddSingleton(MsOptions.Create(host.Options))
            .AddSingleton(host.Manager)
            .BuildServiceProvider();
        var output = new StringWriter();
        var exitCode = await McpCommands.ListToolsAsync(services, args, output, CancellationToken.None);
        return (exitCode, output.ToString());
    }
}
