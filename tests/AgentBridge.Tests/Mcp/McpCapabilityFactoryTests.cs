using System.Text.Json;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AgentBridge.Tests.Mcp;

/// <summary>Rules for turning a Type "mcp" entry into a capability. Most use a hand-made tool, so no server is started.</summary>
public sealed class McpCapabilityFactoryTests
{
    private const string Key = "AgentBridge:Capabilities:code.search";

    private static readonly Tool SearchTool = new()
    {
        Name = "search_code",
        Description = "Searches source files under root. Use this tool before editing code.",
        InputSchema = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object",
             "properties":{
               "query":{"type":"string","description":"Text to search for."},
               "max_results":{"type":"integer","description":"Largest number of matches."},
               "root":{"type":"string","description":"Absolute folder to search in."},
               "order":{"type":"string","enum":["asc","desc"]},
               "filter":{"type":["string","integer"]}},
             "required":["query","root"]}
            """),
    };

    [Fact]
    public async Task Exposes_the_schema_properties_with_their_types_descriptions_and_required_list()
    {
        await using var host = new McpTestHost();

        var descriptor = Build(host, Entry()).Descriptor;

        Assert.Equal("code.search", descriptor.Name);
        Assert.Equal("Finds where a business term appears in the code.", descriptor.Description);
        Assert.Equal(["query", "max_results", "root", "order"], descriptor.Parameters.Keys);
        AssertParameter(descriptor, "query", ParameterTypes.String, required: true, "Text to search for.");
        AssertParameter(descriptor, "max_results", ParameterTypes.Integer, required: false, "Largest number of matches.");
        AssertParameter(descriptor, "root", ParameterTypes.String, required: true, "Absolute folder to search in.");
        AssertParameter(descriptor, "order", ParameterTypes.String, required: false, "");
        Assert.Equal("asc|desc", descriptor.Parameters["order"].Pattern);
        Assert.Equal(TimeSpan.FromSeconds(60), descriptor.Timeout);
        Assert.Equal(OutputFormats.Json, descriptor.Output);
        Assert.False(descriptor.RequiresTarget);
        Assert.Null(descriptor.UseWhen);
        // "filter" has no single simple type: optional, so it is left out with a warning.
        Assert.Contains(host.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'filter'"));
    }

    [Fact]
    public async Task FixedArgs_are_hidden_from_the_cloud_and_converted_to_the_schema_type()
    {
        await using var host = new McpTestHost();

        var descriptor = Build(host, Entry(e =>
        {
            e.FixedArgs["root"] = "D:/repos/app";
            e.FixedArgs["max_results"] = "25";
        })).Descriptor;

        Assert.Equal(["query", "order"], descriptor.Parameters.Keys);
    }

    [Theory]
    [InlineData("rootPath", "D:/x", $"{Key}:FixedArgs:rootPath is not a property of tool 'search_code'; its properties are query, max_results, root, order, filter.")]
    [InlineData("max_results", "many", $"{Key}:FixedArgs:max_results is 'many', which is not a valid integer as the tool's input schema requires.")]
    [InlineData("root", "{targetPath}", $"{Key}:FixedArgs:root uses {{targetPath}}, which needs a target: set RequiresTarget to true and configure the folders under AgentBridge:Targets.")]
    public async Task Invalid_FixedArgs_are_configuration_errors(string name, string value, string expected)
    {
        await using var host = new McpTestHost();

        var errors = BuildErrors(host, Entry(e => e.FixedArgs[name] = value));

        Assert.Equal(expected, Assert.Single(errors));
    }

    [Fact]
    public async Task TargetPath_placeholder_needs_a_target_and_a_string_property()
    {
        await using var host = new McpTestHost();

        var capability = Build(host, Entry(e =>
        {
            e.RequiresTarget = true;
            e.FixedArgs["root"] = "{targetPath}/src";
        }));
        var errors = BuildErrors(host, Entry(e =>
        {
            e.RequiresTarget = true;
            e.FixedArgs["root"] = "{targetPath}";
            e.FixedArgs["max_results"] = "{targetPath}";
        }));

        Assert.True(capability.Descriptor.RequiresTarget);
        Assert.DoesNotContain("root", capability.Descriptor.Parameters.Keys);
        Assert.Equal($"{Key}:FixedArgs:max_results uses {{targetPath}}, but the tool expects integer, not a path string.", Assert.Single(errors));
    }

    [Fact]
    public async Task A_key_in_both_FixedArgs_and_Params_is_an_error()
    {
        await using var host = new McpTestHost();

        var errors = BuildErrors(host, Entry(e =>
        {
            e.FixedArgs["root"] = "D:/repos/app";
            e.Params["query"] = new ParameterOptions();
            e.Params["root"] = new ParameterOptions();
        }));

        Assert.Equal($"{Key}:FixedArgs:root is also in Params; a fixed arg is never sent by the cloud, so remove it from Params.", Assert.Single(errors));
    }

    [Fact]
    public async Task Params_is_an_allowlist_whose_description_and_pattern_win()
    {
        await using var host = new McpTestHost();

        var descriptor = Build(host, Entry(e =>
        {
            e.FixedArgs["root"] = "D:/repos/app";
            e.Params["query"] = new ParameterOptions { Description = "A business term, e.g. Invoice.", Pattern = "[A-Za-z ]{2,40}", Example = "Invoice" };
            e.Params["order"] = new ParameterOptions();
        })).Descriptor;

        Assert.Equal(["query", "order"], descriptor.Parameters.Keys);
        var query = descriptor.Parameters["query"];
        Assert.Equal(ParameterTypes.String, query.Type);
        Assert.True(query.Required);
        Assert.Equal("A business term, e.g. Invoice.", query.Description);
        Assert.Equal("[A-Za-z ]{2,40}", query.Pattern);
        Assert.Equal("Invoice", query.Example!.GetValue<string>());
        Assert.Equal("asc|desc", descriptor.Parameters["order"].Pattern);
        Assert.Equal("""{"query":"Invoice"}""", descriptor.ExampleArgs!.ToJsonString());
    }

    [Fact]
    public async Task Params_entries_must_name_schema_properties()
    {
        await using var host = new McpTestHost();

        var errors = BuildErrors(host, Entry(e =>
        {
            e.FixedArgs["root"] = "D:/repos/app";
            e.Params["query"] = new ParameterOptions();
            e.Params["Query"] = new ParameterOptions();
        }));

        Assert.Equal($"{Key}:Params:Query is not a property of tool 'search_code'; its properties are query, max_results, root, order, filter.", Assert.Single(errors));
    }

    [Fact]
    public async Task A_required_property_that_is_neither_exposed_nor_fixed_is_an_error()
    {
        await using var host = new McpTestHost();

        var errors = BuildErrors(host, Entry(e => e.Params["query"] = new ParameterOptions()));

        Assert.Equal($"{Key}: tool 'search_code' requires 'root', which is neither in Params nor in FixedArgs; add it to one of them.", Assert.Single(errors));
    }

    [Fact]
    public async Task A_property_without_a_simple_type_cannot_be_exposed_on_purpose()
    {
        await using var host = new McpTestHost();

        var errors = BuildErrors(host, Entry(e =>
        {
            e.FixedArgs["root"] = "D:/repos/app";
            e.Params["query"] = new ParameterOptions();
            e.Params["filter"] = new ParameterOptions();
        }));

        Assert.Equal($"{Key}: property 'filter' of tool 'search_code' has no single simple type in its input schema "
            + """({"type":["string","integer"]}); set it in FixedArgs instead and remove it from Params.""", Assert.Single(errors));
    }

    [Fact]
    public async Task Without_a_configured_description_the_tool_description_is_used_with_a_warning()
    {
        await using var host = new McpTestHost();

        var descriptor = Build(host, Entry(e => e.Description = null)).Descriptor;

        Assert.Equal(SearchTool.Description, descriptor.Description);
        var warning = Assert.Single(host.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("untrusted"));
        Assert.Contains($"{Key}:Description is not set", warning.Message);
    }

    [Fact]
    public async Task UseWhen_timeout_and_example_args_come_from_configuration()
    {
        await using var host = new McpTestHost();

        var descriptor = Build(host, Entry(e =>
        {
            e.FixedArgs["root"] = "D:/repos/app";
            e.UseWhen = "The user asks where a business rule is implemented.";
            e.TimeoutSeconds = 120;
            e.ExampleArgs["query"] = "Invoice";
            e.ExampleArgs["max_results"] = "10";
        })).Descriptor;

        Assert.Equal("The user asks where a business rule is implemented.", descriptor.UseWhen);
        Assert.Equal(TimeSpan.FromSeconds(120), descriptor.Timeout);
        Assert.Equal("""{"query":"Invoice","max_results":10}""", descriptor.ExampleArgs!.ToJsonString());
    }

    [Fact]
    public async Task Example_args_must_be_accepted_args_of_the_right_type()
    {
        await using var host = new McpTestHost();

        var errors = BuildErrors(host, Entry(e =>
        {
            e.FixedArgs["root"] = "D:/repos/app";
            e.ExampleArgs["root"] = "D:/other";
            e.ExampleArgs["max_results"] = "ten";
        }));

        Assert.Equal(
            [
                $"{Key}:ExampleArgs:root is not an arg the cloud may send; accepted: query, max_results, order.",
                $"{Key}:ExampleArgs:max_results is 'ten', which is not a valid integer.",
            ],
            errors);
    }

    // ---- server and tool lookup ----

    [Fact]
    public async Task Server_must_be_named_configured_and_enabled_and_nothing_is_started_otherwise()
    {
        await using var host = new McpTestHost(o => o.McpServers["off"] = new McpServerOptions { Command = "dotnet", Enabled = false });

        var missing = host.CreateErrors("code.search", new CapabilityOptions { Type = "mcp", Tool = "echo" });
        var unknown = host.CreateErrors("code.search", new CapabilityOptions { Type = "mcp", Server = "nope", Tool = "echo" });
        var disabled = host.CreateErrors("code.search", new CapabilityOptions { Type = "mcp", Server = "off", Tool = "echo" });
        var noTool = host.CreateErrors("code.search", new CapabilityOptions { Type = "mcp", Server = "fixture" });

        Assert.Equal($"{Key}:Server is required: the name of a server under AgentBridge:McpServers. Configured servers: fixture, off.", Assert.Single(missing));
        Assert.Equal($"{Key}:Server 'nope' is not configured under AgentBridge:McpServers. Configured servers: fixture, off.", Assert.Single(unknown));
        Assert.Equal($"{Key}:Server 'off' is disabled (AgentBridge:McpServers:off:Enabled is false); enable the server or disable this capability.", Assert.Single(disabled));
        Assert.Equal($"{Key}:Tool is required: a tool name exactly as 'AgentBridge mcp-tools fixture' lists it.", Assert.Single(noTool));
        Assert.Empty(host.StartedPids());
    }

    [Fact]
    public async Task An_unknown_tool_lists_the_available_tools_and_points_to_mcp_tools()
    {
        await using var host = new McpTestHost();

        var errors = host.CreateErrors("code.search", new CapabilityOptions { Type = "mcp", Server = "fixture", Tool = "Echo" });

        Assert.Equal($"{Key}:Tool 'Echo' is not a tool of MCP server 'fixture'; its tools are echo, search_code, structured, info, fail, slow, crash, big, mixed. "
            + "Run 'AgentBridge mcp-tools fixture' to see each tool's description and input schema.", Assert.Single(errors));
    }

    [Fact]
    public async Task A_server_that_fails_to_start_is_named_with_the_reason_and_started_only_once()
    {
        await using var host = new McpTestHost();
        host.Options.McpServers["broken"] = host.FixtureServer("--fail-start");

        var first = host.CreateErrors("code.search", new CapabilityOptions { Type = "mcp", Server = "broken", Tool = "echo" });
        var second = host.CreateErrors("code.other", new CapabilityOptions { Type = "mcp", Server = "broken", Tool = "echo" });

        Assert.StartsWith($"{Key}:Server 'broken' could not be used: MCP server 'broken' could not be started with", Assert.Single(first));
        Assert.Contains("exit code: 3", first[0]);
        Assert.StartsWith("AgentBridge:Capabilities:code.other:Server 'broken' could not be used:", Assert.Single(second));
        Assert.Single(host.StartedPids());
    }

    [Fact]
    public async Task The_registry_accepts_an_mcp_capability_with_snake_case_parameters()
    {
        await using var host = new McpTestHost(o => o.Capabilities["code.search"] = new CapabilityOptions
        {
            Type = "MCP",
            Server = "Fixture",
            Tool = "search_code",
            Description = "Finds where a business term appears in the code.",
            FixedArgs = { ["root"] = "D:/repos/app" },
        });

        var registry = new CapabilityRegistry([], [host.Factory], MsOptions.Create(host.Options));

        var capability = Assert.Single(registry.All);
        Assert.Equal("code.search", capability.Descriptor.Name);
        Assert.Equal(["query", "max_results"], capability.Descriptor.Parameters.Keys);
    }

    [Fact]
    public async Task The_registry_reports_mcp_configuration_errors_and_skips_disabled_entries()
    {
        await using var host = new McpTestHost(o =>
        {
            // "mixed" has no description of its own and the entry gives none.
            o.Capabilities["code.mixed"] = new CapabilityOptions { Type = "mcp", Server = "fixture", Tool = "mixed" };
            o.Capabilities["code.off"] = new CapabilityOptions { Type = "mcp", Server = "nope", Tool = "x", Enabled = false };
        });

        var ex = Assert.Throws<OptionsValidationException>(() => new CapabilityRegistry([], [host.Factory], MsOptions.Create(host.Options)));

        Assert.Equal(
            ["AgentBridge:Capabilities:code.mixed:Description is empty. Every capability needs a description the cloud can understand."],
            ex.Failures);
    }

    private static CapabilityOptions Entry(Action<CapabilityOptions>? configure = null)
    {
        var entry = new CapabilityOptions
        {
            Type = "mcp",
            Server = McpTestHost.Server,
            Tool = SearchTool.Name,
            Description = "Finds where a business term appears in the code.",
        };
        configure?.Invoke(entry);
        return entry;
    }

    private static ICapability Build(McpTestHost host, CapabilityOptions entry)
    {
        var errors = new List<string>();
        var capability = host.Factory.Create("code.search", entry, McpTestHost.Server, SearchTool, errors);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        return capability!;
    }

    private static List<string> BuildErrors(McpTestHost host, CapabilityOptions entry)
    {
        var errors = new List<string>();
        Assert.Null(host.Factory.Create("code.search", entry, McpTestHost.Server, SearchTool, errors));
        return errors;
    }

    private static void AssertParameter(CapabilityDescriptor descriptor, string name, string type, bool required, string description)
    {
        var parameter = descriptor.Parameters[name];
        Assert.Equal(type, parameter.Type);
        Assert.Equal(required, parameter.Required);
        Assert.Equal(description, parameter.Description);
    }
}
