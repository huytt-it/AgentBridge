using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using AgentBridge.Processing;
using AgentBridge.Tests.TestSupport;
using static AgentBridge.Tests.Cli.CliTestSupport;

namespace AgentBridge.Tests.Cli;

public sealed class CliCapabilityTests : IDisposable
{
    private const string Secret = "s3cr3t-token-value";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string JobDirectory => _temp.Combine("job");
    private string ArtifactPath(string fileName) => Path.Combine(JobDirectory, "artifacts", fileName);

    private async Task<CapabilityResult> RunAsync(CapabilityOptions options, string argsJson = "{}", string? targetPath = null)
    {
        var capability = CreateValid(options);
        return await capability.ExecuteAsync(Context(_temp, argsJson, targetPath), Guard());
    }

    private static CapabilityOptions BacklogToFile()
    {
        var options = Entry("markdown", "backlog", "--project", "{project}", "--out", "{outputFile}");
        options.Params["project"] = Param(required: true);
        options.Params["status"] = Param();
        options.OptionalArgs["status"] = ["--status", "{status}"];
        return options;
    }

    // ---- output modes ----

    [Fact]
    public async Task Markdown_with_an_output_file_publishes_what_the_tool_wrote()
    {
        var result = await RunAsync(BacklogToFile(), """{"project":"Demo"}""");

        Assert.True(result.Succeeded, result.Error?.Message);
        var markdown = File.ReadAllText(ArtifactPath("result.md"));
        Assert.Contains("# Backlog: Demo", markdown);
        Assert.Contains("Đăng nhập và phân quyền", markdown);
        Assert.Contains("BL-202", markdown);
        Assert.Null(result.Data);
        Assert.Matches(@"^Wrote result\.md \(\d+(\.\d)? (bytes|KB)\)\.$", result.Summary);
        Assert.True(File.Exists(Path.Combine(JobDirectory, "stdout.log")));
        Assert.True(File.Exists(Path.Combine(JobDirectory, "stderr.log")));
    }

    [Fact]
    public async Task Optional_args_reach_the_tool_only_when_sent()
    {
        var result = await RunAsync(BacklogToFile(), """{"project":"Demo","status":"Done"}""");

        Assert.True(result.Succeeded, result.Error?.Message);
        var markdown = File.ReadAllText(ArtifactPath("result.md"));
        Assert.Contains("BL-202", markdown);
        Assert.DoesNotContain("BL-101", markdown);
    }

    [Fact]
    public async Task Markdown_without_an_output_file_takes_stdout_as_result_md()
    {
        var options = Entry("markdown", "backlog", "--project", "{project}");
        options.Params["project"] = Param(required: true);

        var result = await RunAsync(options, """{"project":"Demo"}""");

        Assert.True(result.Succeeded, result.Error?.Message);
        var markdown = File.ReadAllText(ArtifactPath("result.md"));
        Assert.Contains("Đăng nhập và phân quyền", markdown);
        Assert.Equal(File.ReadAllText(Path.Combine(JobDirectory, "stdout.log")), markdown);
        Assert.StartsWith("Wrote result.md (", result.Summary);
    }

    [Fact]
    public async Task Csv_without_an_output_file_takes_stdout_as_result_csv()
    {
        var options = Entry("csv", "backlog", "--project", "Demo");

        var result = await RunAsync(options);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.True(File.Exists(ArtifactPath("result.csv")));
        Assert.False(File.Exists(ArtifactPath("result.md")));
        Assert.StartsWith("Wrote result.csv (", result.Summary);
    }

    [Fact]
    public async Task A_missing_output_file_is_a_failure()
    {
        var result = await RunAsync(Entry("markdown", "echo-args", "{outputFile}"));

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.ExecutionFailed, result.Error!.Code);
        Assert.Contains("did not write its output file", result.Error.Message);
    }

    [Fact]
    public async Task Json_output_is_returned_inline()
    {
        var result = await RunAsync(Entry("json", "json"));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal("DEMO", result.Data!["project"]!.GetValue<string>());
        Assert.Equal(3, result.Data["count"]!.GetValue<int>());
        Assert.Equal("Tiếng Việt có dấu", result.Data["note"]!.GetValue<string>());
        Assert.False(File.Exists(ArtifactPath("result.json")));
    }

    [Fact]
    public async Task Json_over_256_KB_becomes_an_artifact()
    {
        var result = await RunAsync(Entry("json", "json-big", "300"));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Null(result.Data);
        Assert.True(new FileInfo(ArtifactPath("result.json")).Length > 256 * 1024);
        Assert.Contains("result.json", result.Summary);
        using var document = JsonDocument.Parse(File.ReadAllText(ArtifactPath("result.json")));
        Assert.Equal(300, document.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Output_that_is_not_json_is_a_failure()
    {
        var result = await RunAsync(Entry("json", "big", "1"));

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.ExecutionFailed, result.Error!.Code);
        Assert.Contains("not valid JSON", result.Error.Message);
    }

    [Fact]
    public async Task Text_output_returns_exit_code_output_and_truncated()
    {
        var result = await RunAsync(Entry("text", "echo-args", "a", "b"));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(0, result.Data!["exitCode"]!.GetValue<int>());
        Assert.Equal("""["a","b"]""", result.Data["output"]!.GetValue<string>().Trim());
        Assert.False(result.Data["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Text_output_keeps_the_last_32_KB()
    {
        var result = await RunAsync(Entry("text", "big", "100"));

        Assert.True(result.Succeeded, result.Error?.Message);
        var output = result.Data!["output"]!.GetValue<string>();
        Assert.True(output.Length <= 32 * 1024);
        Assert.Contains("line-000100", output);
        Assert.DoesNotContain("line-000001 ", output);
        Assert.True(result.Data["truncated"]!.GetValue<bool>());
        Assert.Equal(100 * 1024, File.ReadAllText(Path.Combine(JobDirectory, "stdout.log")).Length);
    }

    // ---- failures ----

    [Fact]
    public async Task A_non_zero_exit_is_a_failure_with_the_last_stderr()
    {
        var result = await RunAsync(Entry("text", "fail", "3", "something went wrong"));

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.ExecutionFailed, result.Error!.Code);
        Assert.Equal("'test.tool' exited with code 3: something went wrong", result.Error.Message);
    }

    [Fact]
    public async Task A_long_stderr_is_cut_to_its_last_2000_characters()
    {
        var message = string.Concat(Enumerable.Range(0, 400).Select(i => $"line{i:D3} "));

        var result = await RunAsync(Entry("text", "fail", "1", message));

        Assert.False(result.Succeeded);
        var text = result.Error!.Message;
        Assert.True(text.Length < 2100, $"{text.Length} characters");
        Assert.EndsWith("line399", text);
        Assert.DoesNotContain("line000", text);
    }

    [Fact]
    public async Task An_executable_that_cannot_be_started_is_a_failure_that_hides_the_path()
    {
        var notAProgram = _temp.Combine(OperatingSystem.IsWindows() ? "tool.exe" : "tool");
        await File.WriteAllTextAsync(notAProgram, "this is text, not a program");
        var options = Entry("text", "json");
        options.Exe = notAProgram;
        options.Args = [];

        var result = await RunAsync(options);

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.ExecutionFailed, result.Error!.Code);
        Assert.Contains("could not be started", result.Error.Message);
        Assert.DoesNotContain(_temp.Path, result.Error.Message);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_a_failure()
    {
        var options = Entry("text", "json");
        options.RequiresTarget = true;

        var result = await RunAsync(options, targetPath: _temp.Combine("gone"));

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.ExecutionFailed, result.Error!.Code);
        Assert.DoesNotContain(_temp.Path, result.Error.Message);
    }

    [Fact]
    public async Task Cancellation_stops_the_tool_and_propagates()
    {
        var capability = CreateValid(Entry("text", "sleep", "120"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => capability.ExecuteAsync(Context(_temp), cancellation.Token));
    }

    // ---- masking ----

    private static CapabilityOptions WithSecrets(CapabilityOptions options)
    {
        options.Environment["SECRET_TOKEN"] = Secret;
        options.Environment["SHORT_VALUE"] = "abc";
        return options;
    }

    [Fact]
    public async Task Environment_values_reach_the_tool_and_are_masked_in_text_output()
    {
        var masked = await RunAsync(WithSecrets(Entry("text", "env", "SECRET_TOKEN")));

        Assert.True(masked.Succeeded, masked.Error?.Message);
        Assert.Equal("***", masked.Data!["output"]!.GetValue<string>().Trim());
        Assert.DoesNotContain(Secret, masked.Data.ToJsonString());
        Assert.Contains(Secret, File.ReadAllText(Path.Combine(JobDirectory, "stdout.log"))); // the local log keeps the raw output
    }

    [Fact]
    public async Task Values_shorter_than_six_characters_are_not_masked()
    {
        var result = await RunAsync(WithSecrets(Entry("text", "env", "SHORT_VALUE")));

        Assert.Equal("abc", result.Data!["output"]!.GetValue<string>().Trim());
    }

    [Fact]
    public async Task Secrets_are_masked_in_error_messages()
    {
        var result = await RunAsync(WithSecrets(Entry("text", "fail", "1", $"denied for {Secret} today")));

        Assert.False(result.Succeeded);
        Assert.Equal("'test.tool' exited with code 1: denied for *** today", result.Error!.Message);
    }

    [Fact]
    public async Task Secrets_are_masked_in_json_data()
    {
        var result = await RunAsync(WithSecrets(Entry("json", "echo-args", Secret, "plain")));

        Assert.Equal("""["***","plain"]""", result.Data!.ToJsonString());
    }

    [Fact]
    public async Task Secrets_are_masked_in_the_artifact_copied_from_stdout()
    {
        var result = await RunAsync(WithSecrets(Entry("markdown", "backlog", "--project", Secret)));

        Assert.True(result.Succeeded, result.Error?.Message);
        var markdown = File.ReadAllText(ArtifactPath("result.md"));
        Assert.Contains("# Backlog: ***", markdown);
        Assert.DoesNotContain(Secret, markdown);
        Assert.Contains("Đăng nhập", markdown);
    }

    [Fact]
    public async Task Secrets_are_masked_in_files_the_tool_wrote()
    {
        var result = await RunAsync(WithSecrets(Entry("markdown", "backlog", "--project", Secret, "--out", "{outputFile}")));

        Assert.True(result.Succeeded, result.Error?.Message);
        var bytes = File.ReadAllBytes(ArtifactPath("result.md"));
        var markdown = new System.Text.UTF8Encoding(false).GetString(bytes);
        Assert.Contains("# Backlog: ***", markdown);
        Assert.DoesNotContain(Secret, markdown);
        Assert.Contains("Đăng nhập và phân quyền", markdown);
        Assert.Matches(@"\(\d", result.Summary);
    }

    [Fact]
    public async Task A_secret_that_contains_another_one_is_masked_as_a_whole()
    {
        var options = Entry("json", "echo-args", "abcdefgh-longer-secret");
        options.Environment["SHORT_SECRET"] = "abcdefgh";
        options.Environment["LONG_SECRET"] = "abcdefgh-longer-secret";

        var result = await RunAsync(options);

        Assert.Equal("""["***"]""", result.Data!.ToJsonString());
    }

    // ---- working directory ----

    [Fact]
    public async Task The_tool_runs_in_the_job_folder_by_default()
    {
        var result = await RunAsync(Entry("text", "backlog", "--project", "P", "--out", "marker.md"));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.True(File.Exists(Path.Combine(JobDirectory, "marker.md")));
    }

    [Fact]
    public async Task WorkingDirectory_is_used_when_there_is_no_target()
    {
        var folder = _temp.Combine("tool-home");
        Directory.CreateDirectory(folder);
        var options = Entry("text", "backlog", "--project", "P", "--out", "marker.md");
        options.WorkingDirectory = folder;

        var result = await RunAsync(options);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.True(File.Exists(Path.Combine(folder, "marker.md")));
        Assert.False(File.Exists(Path.Combine(JobDirectory, "marker.md")));
    }

    [Fact]
    public async Task The_target_path_is_the_working_directory_when_a_target_is_required()
    {
        var configured = _temp.Combine("tool-home");
        var target = _temp.Combine("project");
        Directory.CreateDirectory(configured);
        Directory.CreateDirectory(target);
        var options = Entry("text", "backlog", "--project", "P", "--out", "marker.md");
        options.RequiresTarget = true;
        options.WorkingDirectory = configured;

        var result = await RunAsync(options, targetPath: target);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.True(File.Exists(Path.Combine(target, "marker.md")));
        Assert.False(File.Exists(Path.Combine(configured, "marker.md")));
    }

    // ---- argument substitution ----

    private static CapabilityOptions EchoWithParams()
    {
        var options = Entry("json", "echo-args", "{project}", "--project={project}", "pre-{project}-post", "--limit={limit}", "--ratio={ratio}", "--status={status}");
        options.Params["project"] = Param(required: true);
        options.Params["limit"] = Param("integer", required: true);
        options.Params["ratio"] = Param("number", required: true);
        options.Params["status"] = Param();
        options.Params["since"] = Param();
        options.Params["verbose"] = Param("boolean");
        options.OptionalArgs["since"] = ["--since", "{since}"];
        options.OptionalArgs["verbose"] = ["--verbose"];
        return options;
    }

    private static string[] Received(CapabilityResult result) => JsonSerializer.Deserialize<string[]>(result.Data!.ToJsonString())!;

    [Fact]
    public async Task A_value_is_one_argument_whatever_it_contains()
    {
        const string project = "A & B | \"C\" < > %PATH% $(x) ; --not-an-option";

        var result = await RunAsync(EchoWithParams(), JsonSerializer.Serialize(new { project, limit = 25, ratio = 1.5 }));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(
            [project, $"--project={project}", $"pre-{project}-post", "--limit=25", "--ratio=1.5"],
            Received(result));
    }

    [Fact]
    public async Task Optional_args_and_items_of_unsent_optional_params_are_left_out_or_added()
    {
        var sent = await RunAsync(
            EchoWithParams(),
            """{"project":"P","limit":1,"ratio":2,"status":"Open","since":"2026-01-01","verbose":true}""");
        var left = await RunAsync(EchoWithParams(), """{"project":"P","limit":1,"ratio":2,"verbose":false}""");

        Assert.Equal(["P", "--project=P", "pre-P-post", "--limit=1", "--ratio=2", "--status=Open", "--since", "2026-01-01", "--verbose"], Received(sent));
        Assert.Equal(["P", "--project=P", "pre-P-post", "--limit=1", "--ratio=2"], Received(left));
    }

    [Fact]
    public async Task Numbers_are_written_with_the_invariant_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            var result = await RunAsync(EchoWithParams(), """{"project":"P","limit":1234567,"ratio":1234.5}""");

            Assert.Equal(["P", "--project=P", "pre-P-post", "--limit=1234567", "--ratio=1234.5"], Received(result));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task TargetPath_is_the_path_of_the_requested_target()
    {
        var target = _temp.Combine("project");
        Directory.CreateDirectory(target);
        var options = Entry("json", "echo-args", "--root", "{targetPath}");
        options.RequiresTarget = true;

        var result = await RunAsync(options, targetPath: target);

        Assert.Equal(["--root", target], Received(result));
    }

    [Fact]
    public async Task A_value_with_a_NUL_character_is_refused_before_the_tool_starts()
    {
        var options = Entry("text", "echo-args", "--project={project}");
        options.Params["project"] = Param(required: true, pattern: @"[\s\S]*");

        var result = await RunAsync(options, """{"project":"x\u0000--injected"}""");

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.InvalidArgs, result.Error!.Code);
        Assert.False(File.Exists(Path.Combine(JobDirectory, "stdout.log")));
    }

    // ---- the default string pattern, as the pipeline enforces it ----

    private static bool IsAccepted(CapabilityDescriptor descriptor, string project)
    {
        var args = JsonSerializer.SerializeToElement(new { project });
        return ArgsValidator.TryValidate(args, descriptor.Parameters, out _, out _);
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("--delete-everything")]
    [InlineData("-")]
    [InlineData("two\nlines")]
    [InlineData("carriage\rreturn")]
    public void The_default_pattern_rejects_leading_dashes_and_line_breaks(string value)
    {
        var options = Entry("text", "echo-args", "{project}");
        options.Params["project"] = Param(required: true);

        Assert.False(IsAccepted(CreateValid(options).Descriptor, value));
    }

    [Theory]
    [InlineData("Project X")]
    [InlineData("a-b")]
    [InlineData("")]
    [InlineData("Đăng nhập")]
    [InlineData("ends-with-dash-")]
    public void The_default_pattern_accepts_ordinary_text(string value)
    {
        var options = Entry("text", "echo-args", "{project}");
        options.Params["project"] = Param(required: true);

        Assert.True(IsAccepted(CreateValid(options).Descriptor, value));
    }

    [Fact]
    public void The_default_pattern_limits_the_length_to_500_characters()
    {
        var options = Entry("text", "echo-args", "{project}");
        options.Params["project"] = Param(required: true);
        var descriptor = CreateValid(options).Descriptor;

        Assert.True(IsAccepted(descriptor, new string('a', 500)));
        Assert.False(IsAccepted(descriptor, new string('a', 501)));
    }

    [Fact]
    public void The_published_example_passes_the_same_validation()
    {
        var options = Entry("text", "echo-args", "{project}", "--limit={limit}");
        options.Params["project"] = Param(required: true);
        options.Params["limit"] = Param("integer", required: true);
        options.ExampleArgs["project"] = "DEMO";
        options.ExampleArgs["limit"] = "5";
        var descriptor = CreateValid(options).Descriptor;

        var example = JsonSerializer.SerializeToElement(descriptor.ExampleArgs);

        Assert.True(ArgsValidator.TryValidate(example, descriptor.Parameters, out _, out var error), error);
    }
}
