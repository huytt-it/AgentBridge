using System.Text.Json.Nodes;
using AgentBridge.Capabilities;
using AgentBridge.Cli;
using AgentBridge.Contracts;
using AgentBridge.Tests.Capabilities;
using AgentBridge.Tests.TestSupport;
using Tools = AgentBridge.Tests.TestSupport.TestTools;
using Microsoft.Extensions.Options;
using static AgentBridge.Tests.Cli.CliTestSupport;

namespace AgentBridge.Tests.Cli;

public sealed class CliCapabilityFactoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>Runs the stand-in tool with no placeholders; each test adds what it needs.</summary>
    private static CapabilityOptions Base(string output = "text")
    {
        var options = Entry(output, "echo-args");
        options.Description = "Lists the open backlog items of a project.";
        return options;
    }

    private static void AssertError(IEnumerable<string> errors, string field, string fragment) =>
        Assert.Contains(errors, error => error.StartsWith($"{Key}:{field} ", StringComparison.Ordinal) && error.Contains(fragment, StringComparison.Ordinal));

    private static CapabilityDescriptor DescriptorOf(CapabilityOptions options) => CreateValid(options).Descriptor;

    // ---- the descriptor ----

    [Fact]
    public void A_complete_entry_becomes_a_capability_with_a_matching_descriptor()
    {
        var options = Base("markdown");
        options.UseWhen = "When you need the current scope.";
        options.RequiresTarget = true;
        options.Args.Add("--project={project}");
        options.Params["project"] = Param(required: true);

        var descriptor = DescriptorOf(options);

        Assert.Equal(Name, descriptor.Name);
        Assert.Equal("Lists the open backlog items of a project.", descriptor.Description);
        Assert.Equal("When you need the current scope.", descriptor.UseWhen);
        Assert.True(descriptor.RequiresTarget);
        Assert.Equal(OutputFormats.Markdown, descriptor.Output);
        Assert.Equal(TimeSpan.FromSeconds(60), descriptor.Timeout);
        var project = Assert.Single(descriptor.Parameters).Value;
        Assert.Equal(ParameterTypes.String, project.Type);
        Assert.True(project.Required);
        Assert.Equal("A test param.", project.Description);
    }

    [Fact]
    public void TimeoutSeconds_is_used_when_set()
    {
        var options = Base();
        options.TimeoutSeconds = 5;

        Assert.Equal(TimeSpan.FromSeconds(5), DescriptorOf(options).Timeout);
    }

    [Fact]
    public void Description_is_required()
    {
        var options = Base();
        options.Description = "  ";

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "Description", "required");
    }

    // ---- Exe ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Exe_is_required(string? exe)
    {
        var options = Base();
        options.Exe = exe;

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "Exe", "required");
    }

    [Fact]
    public void An_absolute_exe_must_exist()
    {
        var options = Base();
        options.Exe = _temp.Combine(OperatingSystem.IsWindows() ? "missing.exe" : "missing");

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "Exe", "does not exist");
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        var options = Base();
        options.Exe = Path.Combine("bin", "tool.exe");

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "Exe", "relative path");
    }

    [Fact]
    public void A_bare_name_is_resolved_on_the_search_path()
    {
        var tool = _temp.Combine(OperatingSystem.IsWindows() ? "faketool.exe" : "faketool");
        File.WriteAllText(tool, "");
        var options = Base();
        options.Exe = "faketool";

        var (capability, errors) = Create(options, searchPath: _temp.Path);

        Assert.Empty(errors);
        Assert.NotNull(capability);
    }

    [Fact]
    public void A_bare_name_that_is_not_on_the_search_path_is_reported()
    {
        var options = Base();
        options.Exe = "no-such-tool-anywhere";

        var (capability, errors) = Create(options, searchPath: _temp.Path);

        Assert.Null(capability);
        AssertError(errors, "Exe", "not found on PATH");
    }

    [Fact]
    public async Task The_resolved_full_path_is_what_gets_started()
    {
        var hostPath = Tools.DotnetPath;
        var options = Base("json");
        options.Exe = Path.GetFileNameWithoutExtension(hostPath);
        var searchPath = Path.IsPathFullyQualified(hostPath) ? Path.GetDirectoryName(hostPath) : null;

        var (capability, errors) = Create(options, searchPath);
        Assert.Empty(errors);
        var result = await capability!.ExecuteAsync(Context(_temp), Guard());

        Assert.True(result.Succeeded, result.Error?.Message);
    }

    [Theory]
    [InlineData("tool.cmd")]
    [InlineData("tool.bat")]
    [InlineData("TOOL.CMD")]
    public void Batch_files_are_refused_with_advice_to_call_the_real_program(string fileName)
    {
        var path = _temp.Combine(fileName);
        File.WriteAllText(path, "@echo off");
        var options = Base();
        options.Exe = path;

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "Exe", "cmd.exe");
        AssertError(errors, "Exe", "\"node\"");
        AssertError(errors, "Exe", "-File");
    }

    [Fact]
    public void A_batch_file_is_refused_even_when_it_does_not_exist()
    {
        var options = Base();
        options.Exe = _temp.Combine("gone.cmd");

        var (_, errors) = Create(options);

        AssertError(errors, "Exe", "batch file");
    }

    [Fact]
    public void A_bare_name_that_resolves_to_a_batch_file_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // PATHEXT lookup is a Windows behaviour
        }
        File.WriteAllText(_temp.Combine("shim.cmd"), "@echo off");
        var options = Base();
        options.Exe = "shim";

        var (capability, errors) = Create(options, searchPath: _temp.Path);

        Assert.Null(capability);
        AssertError(errors, "Exe", "batch file");
    }

    // ---- Output ----

    [Theory]
    [InlineData(null, "text")]
    [InlineData("", "text")]
    [InlineData("markdown", "markdown")]
    [InlineData("csv", "csv")]
    [InlineData("json", "json")]
    [InlineData("text", "text")]
    [InlineData("Markdown", "markdown")]
    [InlineData("CSV", "csv")]
    public void Output_maps_to_an_output_format(string? configured, string expected)
    {
        var options = Base();
        options.Output = configured;

        Assert.Equal(expected, DescriptorOf(options).Output);
    }

    [Fact]
    public void An_unknown_output_is_reported_with_the_accepted_values()
    {
        var options = Base();
        options.Output = "xml";

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "Output", "markdown, csv, json, text");
    }

    // ---- Args templates ----

    [Fact]
    public void An_unknown_placeholder_is_reported_with_the_declared_params()
    {
        var options = Base();
        options.Args.Add("--project={nope}");
        options.Params["project"] = Param();

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, $"Args[{options.Args.Count - 1}]", "{nope} is not a declared param. Declared: project");
    }

    [Theory]
    [InlineData("--project={project")]
    [InlineData("--project=project}")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("{}")]
    [InlineData("{{project}}")]
    [InlineData("{ project }")]
    [InlineData("{pro ject}")]
    [InlineData("{project}}")]
    public void Unbalanced_braces_and_bad_placeholders_are_reported(string template)
    {
        var options = Base();
        options.Args.Add(template);
        options.Params["project"] = Param();
        options.OptionalArgs["project"] = ["{project}"];

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, $"Args[{options.Args.Count - 1}]", template);
    }

    [Fact]
    public void A_placeholder_may_be_the_whole_item_or_part_of_it()
    {
        var options = Base();
        options.Args.AddRange(["{project}", "--project={project}", "prefix-{project}-suffix", "{project}{project}"]);
        options.Params["project"] = Param(required: true);

        Assert.NotNull(CreateValid(options));
    }

    [Fact]
    public void TargetPath_needs_RequiresTarget()
    {
        var options = Base();
        options.Args.Add("{targetPath}");

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, $"Args[{options.Args.Count - 1}]", "RequiresTarget");

        options.RequiresTarget = true;
        Assert.NotNull(CreateValid(options));
    }

    [Theory]
    [InlineData("markdown", true)]
    [InlineData("csv", true)]
    [InlineData("json", false)]
    [InlineData("text", false)]
    public void OutputFile_is_only_for_markdown_and_csv(string output, bool allowed)
    {
        var options = Base(output);
        options.Args.AddRange(["--out", "{outputFile}"]);

        var (capability, errors) = Create(options);

        Assert.Equal(allowed, capability is not null);
        if (!allowed)
        {
            AssertError(errors, $"Args[{options.Args.Count - 1}]", "markdown or csv");
        }
    }

    [Fact]
    public void A_required_param_must_appear_in_Args()
    {
        var options = Base();
        options.Params["project"] = Param(required: true);

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "Params:project", "required but no Args item uses {project}");

        // OptionalArgs is not enough: a required param is always sent, so it belongs in Args.
        options.OptionalArgs["project"] = ["--project", "{project}"];
        var (_, withOptional) = Create(options);
        AssertError(withOptional, "Params:project", "required but no Args item uses {project}");
    }

    [Fact]
    public void An_optional_param_may_be_in_Args_or_OptionalArgs_but_not_nowhere()
    {
        var inArgs = Base();
        inArgs.Args.Add("--status={status}");
        inArgs.Params["status"] = Param();
        Assert.NotNull(CreateValid(inArgs));

        var inOptionalArgs = Base();
        inOptionalArgs.Params["status"] = Param();
        inOptionalArgs.OptionalArgs["status"] = ["--status", "{status}"];
        Assert.NotNull(CreateValid(inOptionalArgs));

        var nowhere = Base();
        nowhere.Params["status"] = Param();
        var (capability, errors) = Create(nowhere);
        Assert.Null(capability);
        AssertError(errors, "Params:status", "used nowhere");
    }

    [Fact]
    public void OptionalArgs_keys_must_be_declared_params()
    {
        var options = Base();
        options.OptionalArgs["since"] = ["--since", "2026-01-01"];

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "OptionalArgs:since", "not a declared param");
    }

    [Fact]
    public void An_OptionalArgs_entry_of_a_value_param_must_use_the_value()
    {
        var options = Base();
        options.Params["since"] = Param();
        options.OptionalArgs["since"] = ["--since"];

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "OptionalArgs:since", "never uses {since}");
    }

    [Fact]
    public void An_OptionalArgs_item_cannot_use_another_optional_param()
    {
        var options = Base();
        options.Params["since"] = Param();
        options.Params["until"] = Param();
        options.OptionalArgs["since"] = ["--since", "{since}", "--until", "{until}"];
        options.OptionalArgs["until"] = ["--until", "{until}"];

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "OptionalArgs:since[3]", "{until} is optional");
    }

    [Fact]
    public void An_OptionalArgs_item_may_use_required_params_and_special_placeholders()
    {
        var options = Base("markdown");
        options.RequiresTarget = true;
        options.Args.Add("{project}");
        options.Params["project"] = Param(required: true);
        options.Params["since"] = Param();
        options.OptionalArgs["since"] = ["--since", "{since}", "--in", "{targetPath}", "--p={project}", "--out={outputFile}"];

        Assert.NotNull(CreateValid(options));
    }

    // ---- Params ----

    [Theory]
    [InlineData("object")]
    [InlineData("array")]
    public void Object_and_array_params_are_refused(string type)
    {
        var options = Base();
        options.Args.Add("{filter}");
        options.Params["filter"] = Param(type);

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "Params:filter:Type", "not supported for cli tools");
    }

    [Fact]
    public void An_unknown_param_type_is_reported()
    {
        var options = Base();
        options.Args.Add("{filter}");
        options.Params["filter"] = Param("date");

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "Params:filter:Type", "string, integer, number, boolean");
    }

    [Fact]
    public void String_integer_number_and_boolean_params_are_supported()
    {
        var options = Base();
        options.Args.AddRange(["{name}", "{limit}", "{ratio}"]);
        options.Params["name"] = Param("string", required: true);
        options.Params["limit"] = Param("integer", required: true);
        options.Params["ratio"] = Param("Number", required: true);
        options.Params["verbose"] = Param("boolean");
        options.OptionalArgs["verbose"] = ["--verbose"];

        var parameters = DescriptorOf(options).Parameters;

        Assert.Equal(ParameterTypes.String, parameters["name"].Type);
        Assert.Equal(ParameterTypes.Integer, parameters["limit"].Type);
        Assert.Equal(ParameterTypes.Number, parameters["ratio"].Type);
        Assert.Equal(ParameterTypes.Boolean, parameters["verbose"].Type);
    }

    [Fact]
    public void A_boolean_can_only_drive_OptionalArgs()
    {
        var asPlaceholder = Base();
        asPlaceholder.Args.Add("--verbose={verbose}");
        asPlaceholder.Params["verbose"] = Param("boolean");
        asPlaceholder.OptionalArgs["verbose"] = ["--verbose"];
        var (_, placeholderErrors) = Create(asPlaceholder);
        AssertError(placeholderErrors, $"Args[{asPlaceholder.Args.Count - 1}]", "{verbose} is a boolean");

        var notWired = Base();
        notWired.Params["verbose"] = Param("boolean");
        var (_, wiringErrors) = Create(notWired);
        AssertError(wiringErrors, "Params:verbose", "OptionalArgs has no entry 'verbose'");

        var required = Base();
        required.Params["verbose"] = Param("boolean", required: true);
        required.OptionalArgs["verbose"] = ["--verbose"];
        var (_, requiredErrors) = Create(required);
        AssertError(requiredErrors, "Params:verbose:Required", "optional switch");

        var inItsOwnEntry = Base();
        inItsOwnEntry.Params["verbose"] = Param("boolean");
        inItsOwnEntry.OptionalArgs["verbose"] = ["--verbose={verbose}"];
        var (_, ownErrors) = Create(inItsOwnEntry);
        AssertError(ownErrors, "OptionalArgs:verbose[0]", "{verbose} is a boolean");
    }

    [Theory]
    [InlineData("targetPath")]
    [InlineData("outputFile")]
    public void The_names_of_special_placeholders_are_reserved(string name)
    {
        var options = Base();
        options.Params[name] = Param();
        options.OptionalArgs[name] = ["--x", $"{{{name}}}"];

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, $"Params:{name}", "reserved name");
    }

    // ---- patterns ----

    [Fact]
    public void A_string_param_without_a_pattern_gets_the_safe_default()
    {
        var options = Base();
        options.Args.Add("--project={project}");
        options.Params["project"] = Param(required: true);

        var pattern = DescriptorOf(options).Parameters["project"].Pattern;

        Assert.Equal(@"^(?!-)[^\r\n]{0,500}\z", pattern);
    }

    [Fact]
    public void A_configured_pattern_is_kept_and_only_strings_have_one()
    {
        var options = Base();
        options.Args.AddRange(["--project={project}", "--limit={limit}"]);
        options.Params["project"] = Param(required: true, pattern: "[A-Z]{2,10}");
        options.Params["limit"] = Param("integer", required: true, pattern: "[0-9]+");

        var parameters = DescriptorOf(options).Parameters;

        Assert.Equal("[A-Z]{2,10}", parameters["project"].Pattern);
        Assert.Null(parameters["limit"].Pattern);
    }

    [Fact]
    public void A_pattern_that_is_not_a_regular_expression_is_reported()
    {
        var options = Base();
        options.Args.Add("--project={project}");
        options.Params["project"] = Param(required: true, pattern: "([unclosed");

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "Params:project:Pattern", "not a valid regular expression");
    }

    [Fact]
    public void A_pattern_that_lets_a_value_start_with_a_dash_is_refused_where_the_value_would_be_an_option()
    {
        var wholeItem = Base();
        wholeItem.Args.Add("{project}");
        wholeItem.Params["project"] = Param(required: true, pattern: @"[\w-]+");
        var (capability, errors) = Create(wholeItem);
        Assert.Null(capability);
        AssertError(errors, "Params:project:Pattern", "(?!-)");

        var afterOption = Base();
        afterOption.Args.Add("--project={project}");
        afterOption.Params["project"] = Param(required: true, pattern: @"[\w-]+");
        Assert.NotNull(CreateValid(afterOption));

        var noDash = Base();
        noDash.Args.Add("{project}");
        noDash.Params["project"] = Param(required: true, pattern: @"(?!-)[\w-]+");
        Assert.NotNull(CreateValid(noDash));
    }

    // ---- WorkingDirectory, Environment ----

    [Fact]
    public void WorkingDirectory_must_exist()
    {
        var options = Base();
        options.WorkingDirectory = _temp.Combine("missing");

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "WorkingDirectory", "does not exist");

        Directory.CreateDirectory(options.WorkingDirectory);
        Assert.NotNull(CreateValid(options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A=B")]
    public void Environment_variable_names_are_checked(string variable)
    {
        var options = Base();
        options.Environment[variable] = "value";

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, $"Environment:{variable}", "not a valid environment variable name");
    }

    // ---- examples ----

    [Fact]
    public void ExampleArgs_are_converted_to_each_params_json_type()
    {
        var options = Base();
        options.Args.AddRange(["--project={project}", "--limit={limit}", "--ratio={ratio}"]);
        options.Params["project"] = Param(required: true);
        options.Params["limit"] = Param("integer", required: true);
        options.Params["ratio"] = Param("number", required: true);
        options.Params["verbose"] = Param("boolean");
        options.OptionalArgs["verbose"] = ["--verbose"];
        options.ExampleArgs["project"] = "DEMO";
        options.ExampleArgs["limit"] = "25";
        options.ExampleArgs["ratio"] = "1.5";
        options.ExampleArgs["verbose"] = "true";

        var example = DescriptorOf(options).ExampleArgs!;

        Assert.Equal("DEMO", example["project"]!.GetValue<string>());
        Assert.Equal(25L, example["limit"]!.GetValue<long>());
        Assert.Equal(1.5m, example["ratio"]!.GetValue<decimal>());
        Assert.True(example["verbose"]!.GetValue<bool>());
        Assert.Equal("""{"project":"DEMO","limit":25,"ratio":1.5,"verbose":true}""", example.ToJsonString());
    }

    [Fact]
    public void A_param_example_is_converted_too()
    {
        var options = Base();
        options.Args.Add("--limit={limit}");
        options.Params["limit"] = Param("integer", required: true, example: "10");

        Assert.Equal(10L, DescriptorOf(options).Parameters["limit"].Example!.GetValue<long>());
    }

    [Fact]
    public void Example_problems_are_reported()
    {
        var options = Base();
        options.Args.AddRange(["--project={project}", "--limit={limit}"]);
        options.Params["project"] = Param(required: true);
        options.Params["limit"] = Param("integer", required: true, example: "many");
        options.ExampleArgs["project"] = "--starts-with-dash";
        options.ExampleArgs["limit"] = "1.5";
        options.ExampleArgs["unknown"] = "x";

        var (capability, errors) = Create(options);

        Assert.Null(capability);
        AssertError(errors, "ExampleArgs:project", "does not match the param's Pattern");
        AssertError(errors, "ExampleArgs:limit", "not a valid integer");
        AssertError(errors, "ExampleArgs:unknown", "not a declared param");
        AssertError(errors, "Params:limit:Example", "not a valid integer");
    }

    // ---- reporting ----

    [Fact]
    public void Every_problem_is_reported_by_its_config_key_in_one_pass()
    {
        var options = new CapabilityOptions
        {
            Type = "cli",
            Exe = "no-such-tool-anywhere",
            Output = "xml",
            Args = ["--x={missing}", "{"],
            Params = { ["filter"] = Param("object") },
            WorkingDirectory = _temp.Combine("missing"),
        };

        var (capability, errors) = Create(options, searchPath: _temp.Path);

        Assert.Null(capability);
        AssertError(errors, "Exe", "not found");
        AssertError(errors, "Description", "required");
        AssertError(errors, "Output", "xml");
        AssertError(errors, "Args[0]", "{missing}");
        AssertError(errors, "Args[1]", "'{'");
        AssertError(errors, "Params:filter:Type", "object");
        AssertError(errors, "WorkingDirectory", "missing");
        Assert.All(errors, error => Assert.StartsWith($"{Key}:", error));
    }

    [Fact]
    public void Errors_are_added_to_the_list_the_caller_passes_in()
    {
        var errors = new List<string> { "an earlier problem" };
        var options = Base();
        options.Exe = null;

        var capability = Factory().Create(Name, options, errors);

        Assert.Null(capability);
        Assert.Equal("an earlier problem", errors[0]);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void The_registry_builds_cli_entries_and_lists_every_problem_at_startup()
    {
        var valid = Base("json");
        valid.Args.Add("--project={project}");
        valid.Params["project"] = Param(required: true);
        var broken = Base();
        broken.Exe = null;
        broken.Description = null;

        var good = CapabilityTestSupport.CreateRegistry(
            CapabilityTestSupport.CreateOptions(o => o.Capabilities[Name] = valid), [Factory()]);
        Assert.True(good.TryGet(Name, out var capability));
        Assert.IsType<CliCapability>(capability);

        var failure = Assert.Throws<OptionsValidationException>(() => CapabilityTestSupport.CreateRegistry(
            CapabilityTestSupport.CreateOptions(o => o.Capabilities["test.broken"] = broken), [Factory()]));
        Assert.Contains(failure.Failures, f => f.StartsWith("AgentBridge:Capabilities:test.broken:Exe "));
        Assert.Contains(failure.Failures, f => f.StartsWith("AgentBridge:Capabilities:test.broken:Description "));
    }

    [Fact]
    public void A_disabled_entry_is_never_created()
    {
        var disabled = Base();
        disabled.Exe = null;
        disabled.Enabled = false;

        var registry = CapabilityTestSupport.CreateRegistry(
            CapabilityTestSupport.CreateOptions(o => o.Capabilities[Name] = disabled), [Factory()]);

        Assert.False(registry.TryGet(Name, out _));
    }
}
