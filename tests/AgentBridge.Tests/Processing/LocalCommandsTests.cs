using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using AgentBridge.Processing;
using AgentBridge.Tests.Processing.Fakes;
using static AgentBridge.Tests.Processing.Fakes.PipelineHarness;

namespace AgentBridge.Tests.Processing;

public sealed class LocalCommandsTests
{
    [Fact]
    public async Task Run_prints_the_response_and_returns_0_when_it_succeeds()
    {
        var ping = Ping((context, _) => Task.FromResult(
            CapabilityResult.Success(new JsonObject { ["echo"] = context.Args.GetProperty("echo").GetString() })));
        using var h = new PipelineHarness(ping);
        await using var services = h.BuildServices();
        var output = new StringWriter();

        var exitCode = await LocalCommands.RunAsync(services, ["agent.ping", "--args", """{"echo":"hi"}"""], output, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var response = JsonSerializer.Deserialize<AgentResponse>(output.ToString(), JsonDefaults.Options)!;
        Assert.Equal(JobStatus.Succeeded, response.Status);
        Assert.Equal("hi", response.Result!["echo"]!.GetValue<string>());
        Assert.StartsWith("local-20261031090000-", response.RequestId);
        Assert.True(Naming.IsValidRequestId(response.RequestId));

        var workDirectory = Path.Combine(h.Paths.LocalDataRoot, "local-runs", response.RequestId);
        Assert.Equal(workDirectory, ping.LastContext!.JobDirectory);
        Assert.True(Directory.Exists(workDirectory));
        Assert.False(AgentBridgePaths.IsSameOrInside(workDirectory, h.Paths.Jobs));
        Assert.Equal(0, h.Jobs.Count);
        Assert.Empty(h.Box.Responses);
    }

    [Fact]
    public async Task Run_passes_the_target()
    {
        var files = new FakeCapability(new CapabilityDescriptor { Name = "test.files", Description = "Files", RequiresTarget = true });
        using var h = new PipelineHarness([files], o => o.Targets["DemoRepo"] = new TargetOptions { Path = Path.GetTempPath(), Description = "Simulator" });
        await using var services = h.BuildServices();

        var exitCode = await LocalCommands.RunAsync(services, ["test.files", "--target", "DemoRepo"], new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("DemoRepo", files.LastContext!.TargetName);
    }

    [Theory]
    [InlineData("code.nothing", ErrorCodes.UnknownCapability)]
    [InlineData("agent.ping --args [1]", ErrorCodes.InvalidArgs)]
    [InlineData("agent.ping --target DemoRepo", ErrorCodes.InvalidRequest)]
    public async Task Run_returns_1_when_the_request_is_rejected(string commandLine, string code)
    {
        using var h = new PipelineHarness(Ping());
        await using var services = h.BuildServices();
        var output = new StringWriter();

        var exitCode = await LocalCommands.RunAsync(services, Split(commandLine), output, CancellationToken.None);

        Assert.Equal(1, exitCode);
        var response = JsonSerializer.Deserialize<AgentResponse>(output.ToString(), JsonDefaults.Options)!;
        Assert.Equal(JobStatus.Rejected, response.Status);
        Assert.Equal(code, response.Error!.Code);
    }

    [Fact]
    public async Task Run_returns_1_when_the_capability_fails()
    {
        using var h = new PipelineHarness(Ping((_, _) => Task.FromResult(CapabilityResult.Failure("broken", "It broke."))));
        await using var services = h.BuildServices();

        var exitCode = await LocalCommands.RunAsync(services, ["agent.ping"], new StringWriter(), CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Run_cancelled_while_running_reports_interrupted()
    {
        var ping = Ping(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return CapabilityResult.Success(null);
        });
        using var h = new PipelineHarness(ping);
        await using var services = h.BuildServices();
        var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();

        var run = LocalCommands.RunAsync(services, ["agent.ping"], output, cancellation.Token);
        await ping.Started.Task;
        cancellation.Cancel();

        Assert.Equal(1, await run);
        var response = JsonSerializer.Deserialize<AgentResponse>(output.ToString(), JsonDefaults.Options)!;
        Assert.Equal(JobStatus.Interrupted, response.Status);
    }

    [Theory]
    [InlineData("", "Name the capability")]
    [InlineData("agent.ping --verbose", "Unknown option '--verbose'")]
    [InlineData("agent.ping --target", "--target needs a value")]
    [InlineData("agent.ping --args {not-json", "--args must be a JSON object")]
    [InlineData("agent.ping extra", "Unexpected argument 'extra'")]
    [InlineData("agent.ping --target a --target b", "--target is given more than once")]
    public async Task Run_returns_2_with_usage_for_bad_command_lines(string commandLine, string message)
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        await using var services = h.BuildServices();
        var output = new StringWriter();

        var exitCode = await LocalCommands.RunAsync(services, Split(commandLine), output, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains(message, output.ToString());
        Assert.Contains("Usage: AgentBridge run <capability>", output.ToString());
        Assert.Equal(0, ping.Executions);
    }

    [Fact]
    public async Task Validate_prints_the_configuration_summary()
    {
        using var h = new PipelineHarness([Ping()], o => o.Targets["DemoRepo"] = new TargetOptions { Path = Path.GetTempPath(), Description = "Simulator" });
        await using var services = h.BuildServices();
        var output = new StringWriter();

        var exitCode = await LocalCommands.ValidateAsync(services, [], output, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains(h.Paths.BoxRoot, text);
        Assert.Contains(h.Paths.LocalDataRoot, text);
        Assert.Contains("DemoRepo", text);
        Assert.Contains("agent.ping - Checks that the agent is reachable.", text);
        Assert.Contains(FakeCatalogProvider.Version, text);
    }

    [Fact]
    public async Task Validate_prints_domains_playbooks_notes_and_hints()
    {
        var export = new FakeCapability(new CapabilityDescriptor { Name = "backlog.export", Description = "Exports the backlog." });
        using var h = new PipelineHarness(Ping(), export);
        h.Registry.Domains =
        [
            new CatalogDomain { Name = "agent", Description = "The agent itself.", Capabilities = ["agent.ping"] },
            new CatalogDomain { Name = "backlog", Capabilities = ["backlog.export"] },
        ];
        h.Registry.Playbooks =
        [
            new CatalogPlaybook
            {
                Name = "backlog-check",
                Goal = "Check the backlog.",
                Steps = [new CatalogPlaybookStep("agent.ping", "Reach the agent."), new CatalogPlaybookStep("backlog.export", "Read the items.")],
            },
        ];
        h.Registry.Notes = ["Playbook 'other' is left out of the catalog: step 1 uses 'code.search', which is disabled."];
        await using var services = h.BuildServices();
        var output = new StringWriter();

        Assert.Equal(0, await LocalCommands.ValidateAsync(services, [], output, CancellationToken.None));

        var lines = output.ToString().ReplaceLineEndings("\n");
        Assert.Contains("""
            Domains (2):
              agent - The agent itself.
                capabilities: agent.ping
              backlog - (no description)
                capabilities: backlog.export
            Playbooks (1):
              backlog-check - Check the backlog.
                1. agent.ping - Reach the agent.
                2. backlog.export - Read the items.
            Notes:
              - Playbook 'other' is left out of the catalog: step 1 uses 'code.search', which is disabled.
            Hints:
              - Domain 'backlog' has no description. Add AgentBridge:Domains:backlog with Description, UseWhen and ExampleQuestions
            """.ReplaceLineEndings("\n"), lines, StringComparison.Ordinal);
        Assert.DoesNotContain("Domain 'agent' has no description", lines, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Validate_says_when_box_root_is_the_box_drive_default(bool isDefault)
    {
        using var h = new PipelineHarness([Ping()], o => o.BoxRoot = isDefault ? "" : o.BoxRoot);
        await using var services = h.BuildServices();
        var output = new StringWriter();

        await LocalCommands.ValidateAsync(services, [], output, CancellationToken.None);

        var text = output.ToString();
        Assert.Equal(isDefault, h.Paths.BoxRootIsDefault);
        Assert.Contains(h.Paths.BoxRoot, text);
        Assert.Equal(isDefault, text.Contains("Box Drive default, because BoxRoot is not set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_json_prints_the_catalog()
    {
        using var h = new PipelineHarness(Ping());
        await using var services = h.BuildServices();
        var output = new StringWriter();

        var exitCode = await LocalCommands.ValidateAsync(services, ["--json"], output, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var catalog = JsonSerializer.Deserialize<Catalog>(output.ToString(), JsonDefaults.Options)!;
        Assert.Equal(FakeCatalogProvider.Version, catalog.CatalogVersion);
        Assert.Equal("agent.ping", Assert.Single(catalog.Capabilities).Name);
    }

    [Fact]
    public async Task Validate_returns_2_for_unknown_options()
    {
        using var h = new PipelineHarness(Ping());
        await using var services = h.BuildServices();
        var output = new StringWriter();

        var exitCode = await LocalCommands.ValidateAsync(services, ["--yaml"], output, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage: AgentBridge validate", output.ToString());
    }

    private static string[] Split(string commandLine) => commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
