using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Tests.TestSupport;
using Tools = AgentBridge.Tests.TestSupport.TestTools;

namespace AgentBridge.Tests.Cli;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string StdoutLog => _temp.Combine("stdout.log");
    private string StderrLog => _temp.Combine("stderr.log");

    private ProcessRunRequest Request(
        string[] toolArgs, int tailChars = 32 * 1024, Dictionary<string, string>? environment = null, string? workingDirectory = null) =>
        new(Tools.DotnetPath, Tools.Args(["cli", .. toolArgs]), workingDirectory ?? _temp.Path,
            environment ?? [], StdoutLog, StderrLog, tailChars);

    private Task<ProcessRunResult> RunAsync(ProcessRunRequest request) => CliTestSupport.Runner.RunAsync(request, CliTestSupport.Guard());

    [Fact]
    public async Task Arguments_arrive_verbatim()
    {
        string[] awkward =
        [
            "plain", "with space", "say \"hi\"", "a&b", "a|b", "a<b>c", "100%", "%PATH%", "$(whoami)", "`x`", "^caret", "!bang!",
            "trailing\\", "back\\slash\\\"quote", "", "Tiếng Việt có dấu", "--flag=\"quoted value\"", "'single'", "a;b", "*.*",
        ];

        var result = await RunAsync(Request(["echo-args", .. awkward]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(awkward, JsonSerializer.Deserialize<string[]>(result.StdoutTail));
    }

    [Fact]
    public async Task Exit_code_and_both_streams_are_returned()
    {
        var result = await RunAsync(Request(["fail", "7", "boom: it broke"]));

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("boom: it broke", result.StderrTail);
        Assert.Equal("", result.StdoutTail);
        Assert.Contains("boom: it broke", File.ReadAllText(StderrLog));
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task Stdin_is_closed_so_a_tool_waiting_for_input_finishes()
    {
        var result = await RunAsync(Request(["read-stdin"]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("eof", result.StdoutTail.Trim());
    }

    [Fact]
    public async Task Output_is_read_as_utf8_and_the_log_keeps_the_bytes()
    {
        var result = await RunAsync(Request(["backlog", "--project", "Tiếng Việt"]));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Đăng nhập và phân quyền", result.StdoutTail);
        Assert.Contains("# Backlog: Tiếng Việt", result.StdoutTail);
        var log = File.ReadAllBytes(StdoutLog);
        Assert.Equal(result.StdoutTail, new UTF8Encoding(false).GetString(log));
        Assert.False(log.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
    }

    [Fact]
    public async Task Only_the_tail_is_kept_in_memory_and_the_logs_are_complete()
    {
        var result = await RunAsync(Request(["big", "200"], tailChars: 2000));

        Assert.True(result.StdoutTruncated);
        Assert.False(result.StderrTruncated);
        Assert.True(result.StdoutTail.Length <= 2000);
        Assert.Contains("line-000200", result.StdoutTail);
        Assert.DoesNotContain("line-000001 ", result.StdoutTail);

        var log = File.ReadAllText(StdoutLog);
        Assert.Equal(200 * 1024, log.Length);
        Assert.StartsWith("line-000001 ", log);
        Assert.Equal(0, new FileInfo(StderrLog).Length);
    }

    [Fact]
    public async Task Output_within_the_limit_is_not_truncated()
    {
        var result = await RunAsync(Request(["big", "3"], tailChars: 10_000));

        Assert.False(result.StdoutTruncated);
        Assert.Equal(3 * 1024, result.StdoutTail.Length);
    }

    [Fact]
    public async Task Environment_is_inherited_and_the_extra_variables_are_added()
    {
        var inherited = "AGENTBRIDGE_TEST_INHERITED_" + Guid.NewGuid().ToString("N");
        var extra = "AGENTBRIDGE_TEST_EXTRA_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(inherited, "from-the-worker");
        try
        {
            var environment = new Dictionary<string, string> { [extra] = "from-the-config" };

            var inheritedResult = await RunAsync(Request(["env", inherited], environment: environment));
            var extraResult = await RunAsync(Request(["env", extra], environment: environment));

            Assert.Equal("from-the-worker", inheritedResult.StdoutTail.Trim());
            Assert.Equal("from-the-config", extraResult.StdoutTail.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable(inherited, null);
        }
    }

    [Fact]
    public async Task The_tool_runs_in_the_given_working_directory()
    {
        var folder = _temp.Combine("work");
        Directory.CreateDirectory(folder);

        var result = await RunAsync(Request(["backlog", "--project", "P", "--out", "marker.md"], workingDirectory: folder));

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(folder, "marker.md")));
    }

    [Fact]
    public async Task Cancelling_kills_the_whole_process_tree_and_throws()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var run = CliTestSupport.Runner.RunAsync(Request(["spawn-and-sleep", "120"]), cancellation.Token);

        var childId = await WaitForChildIdAsync(StdoutLog);
        Assert.False(IsGone(childId), "the child should be running before the cancellation");

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(await WaitUntilGoneAsync(childId), "the child process survived the cancellation");
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_starts_nothing()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CliTestSupport.Runner.RunAsync(Request(["sleep", "30"]), new CancellationToken(canceled: true)));

        Assert.False(File.Exists(StdoutLog));
    }

    [Fact]
    public async Task A_timeout_stops_a_tool_that_does_not_finish()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CliTestSupport.Runner.RunAsync(Request(["sleep", "120"]), timeout.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task An_executable_that_cannot_be_started_throws_Win32Exception()
    {
        var notAProgram = _temp.Combine(OperatingSystem.IsWindows() ? "tool.exe" : "tool");
        await File.WriteAllTextAsync(notAProgram, "this is text, not a program");
        var request = new ProcessRunRequest(notAProgram, [], _temp.Path, new Dictionary<string, string>(), StdoutLog, StderrLog, 1000);

        await Assert.ThrowsAsync<Win32Exception>(() => CliTestSupport.Runner.RunAsync(request, CliTestSupport.Guard()));
    }

    private static async Task<int> WaitForChildIdAsync(string stdoutLog)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(stdoutLog) && int.TryParse(CliTestSupport.ReadShared(stdoutLog).Trim(), out var id))
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
            if (IsGone(processId))
            {
                return true;
            }
            await Task.Delay(50);
        }
        return false;
    }

    private static bool IsGone(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
