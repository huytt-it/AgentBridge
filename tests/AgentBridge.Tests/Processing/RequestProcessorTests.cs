using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using AgentBridge.Tests.Processing.Fakes;
using Microsoft.Extensions.Logging;
using static AgentBridge.Tests.Processing.Fakes.PipelineHarness;

namespace AgentBridge.Tests.Processing;

public sealed class RequestProcessorTests
{
    [Fact]
    public async Task Runs_a_request_then_saves_publishes_marks_and_archives()
    {
        var ping = Ping((context, _) => Task.FromResult(
            CapabilityResult.Success(new JsonObject { ["echo"] = context.Args.GetProperty("echo").GetString() }, "pong")));
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1", json =>
        {
            json["args"] = new JsonObject { ["echo"] = "hi" };
            json["correlationId"] = "c1";
            json["purpose"] = "check reachability";
        });

        await h.ProcessInboxAsync();

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Succeeded, response.Status);
        Assert.Equal("pong", response.Summary);
        Assert.Equal("hi", response.Result!["echo"]!.GetValue<string>());
        Assert.Null(response.Error);
        Assert.Equal("agent.ping", response.Capability);
        Assert.Equal("c1", response.CorrelationId);
        Assert.Equal(new AgentInfo("test-agent", AgentVersion.Current), response.Agent);
        Assert.Equal(FakeCatalogProvider.Version, response.CatalogVersion);
        Assert.Equal(Start, response.StartedAtUtc);
        Assert.Equal(Start, response.CompletedAtUtc);
        Assert.Same(response, h.Jobs.ResponseOf("r1"));
        Assert.True(h.Jobs.IsPublished("r1"));
        Assert.Equal(["r1.json"], h.Box.Archived);
        Assert.Empty(h.Box.InboxFileNames);
        Assert.Equal(h.Jobs.GetJobDirectory("r1"), ping.LastContext!.JobDirectory);
        var log = Assert.Single(h.Logger.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith("Request r1"));
        Assert.Contains("Succeeded", log.Message);
        Assert.Contains("check reachability", log.Message);
    }

    [Fact]
    public async Task Reads_requests_that_start_with_a_utf8_bom()
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        h.Box.AddFile("r1.json", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(RequestJson("r1"))], Start);

        await h.ProcessInboxAsync();

        Assert.Equal(JobStatus.Succeeded, h.Box.Responses["r1"].Status);
    }

    [Fact]
    public async Task Duplicate_with_same_bytes_is_archived_and_never_run_again()
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        h.Box.AddRequest("r1.json", RequestJson("r1"), Start);
        await h.ProcessInboxAsync();

        h.Box.AddRequest("r1-copy.json", RequestJson("r1"), Start);
        await h.ProcessInboxAsync();

        Assert.Equal(1, ping.Executions);
        Assert.Equal(1, h.Box.PublishCount);
        Assert.Equal(["r1.json", "r1-copy.json"], h.Box.Archived);
        Assert.Empty(h.Box.DeadLettered);
    }

    [Fact]
    public async Task Duplicate_with_same_bytes_publishes_a_saved_response_that_was_not_published()
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        var saved = new AgentResponse { RequestId = "r1", Status = JobStatus.Succeeded, CompletedAtUtc = Start };
        h.Jobs.Seed("r1", RequestJson("r1"), saved, published: false, Start);
        h.Box.AddRequest("r1.json", RequestJson("r1"), Start);

        await h.ProcessInboxAsync();

        Assert.Same(saved, h.Box.Responses["r1"]);
        Assert.True(h.Jobs.IsPublished("r1"));
        Assert.Equal(["r1.json"], h.Box.Archived);
        Assert.Equal(0, ping.Executions);
    }

    [Fact]
    public async Task Duplicate_with_different_bytes_is_dead_lettered_and_the_original_is_untouched()
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        h.Box.AddRequest("r1.json", RequestJson("r1"), Start);
        await h.ProcessInboxAsync();
        var original = h.Box.Responses["r1"];

        h.Box.AddRequest("r1-other.json", RequestJson("r1", configure: j => j["args"] = new JsonObject { ["echo"] = "x" }), Start);
        await h.ProcessInboxAsync();

        var reason = h.Box.DeadLettered["r1-other.json"];
        Assert.Contains(ErrorCodes.DuplicateConflict, reason);
        Assert.Contains("requestId r1 was already used for a different request", reason);
        Assert.Contains("new requestId", reason);
        Assert.Same(original, h.Box.Responses["r1"]);
        Assert.Same(original, h.Jobs.ResponseOf("r1"));
        Assert.Equal(1, h.Box.PublishCount);
        Assert.Equal(1, ping.Executions);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("dead-letter"));
    }

    [Fact]
    public async Task Invalid_json_stays_in_inbox_while_it_may_still_be_syncing_then_goes_to_dead_letter()
    {
        using var h = new PipelineHarness(Ping());
        h.Box.AddRequest("partial.json", """{"schemaVersion":1,"requestId":""", Start.AddSeconds(-10));

        await h.ProcessInboxAsync();

        Assert.Equal(["partial.json"], h.Box.InboxFileNames);
        Assert.Empty(h.Box.DeadLettered);

        h.Time.Advance(TimeSpan.FromSeconds(h.Options.IncompleteFileGraceSeconds));
        await h.ProcessInboxAsync();

        Assert.Empty(h.Box.InboxFileNames);
        Assert.Contains("not a valid request", h.Box.DeadLettered["partial.json"]);
        Assert.Equal(0, h.Jobs.Count);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("")]
    [InlineData("""{"schemaVersion":"one","requestId":"r1","capability":"agent.ping"}""")]
    public async Task Old_files_that_are_not_requests_go_to_dead_letter(string content)
    {
        using var h = new PipelineHarness(Ping());
        h.Box.AddRequest("bad.json", content, Start.AddMinutes(-5));

        await h.ProcessInboxAsync();

        Assert.Contains("not a valid request", h.Box.DeadLettered["bad.json"]);
        Assert.Empty(h.Box.Responses);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("CON")]
    [InlineData("has space")]
    public async Task Unsafe_request_ids_go_to_dead_letter_without_a_job(string requestId)
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        h.Box.AddRequest("bad.json", RequestJson(requestId), Start);

        await h.ProcessInboxAsync();

        Assert.Contains("requestId", h.Box.DeadLettered["bad.json"]);
        Assert.Contains("letters, digits", h.Box.DeadLettered["bad.json"]);
        Assert.Equal(0, h.Jobs.Count);
        Assert.Empty(h.Box.Responses);
        Assert.Equal(0, ping.Executions);
    }

    [Fact]
    public async Task Locked_file_is_skipped_until_a_later_poll()
    {
        using var h = new PipelineHarness(Ping());
        h.AddRequest("r1");
        h.Box.LockedFiles.Add("r1.json");

        await h.ProcessInboxAsync();

        Assert.Equal(["r1.json"], h.Box.InboxFileNames);
        Assert.Equal(0, h.Jobs.Count);

        h.Box.LockedFiles.Clear();
        await h.ProcessInboxAsync();

        Assert.Equal(JobStatus.Succeeded, h.Box.Responses["r1"].Status);
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"requestId":"r1","capability":"agent.ping"}""", ErrorCodes.UnsupportedSchemaVersion, "accepts schemaVersion 1")]
    [InlineData("""{"requestId":"r1","capability":"agent.ping"}""", ErrorCodes.UnsupportedSchemaVersion, "schemaVersion 0")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":" "}""", ErrorCodes.InvalidRequest, "agent.ping, test.files")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping","expiresAtUtc":"2026-10-31T09:00:00Z"}""", ErrorCodes.Expired, "2026-10-31T09:00:00Z")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"code.nothing"}""", ErrorCodes.UnknownCapability, "Available capabilities: agent.ping, test.files. Re-read catalog.json")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"test.files","args":{"count":1}}""", ErrorCodes.TargetRequired, "Available targets: DemoRepo, Docs")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"test.files","target":"Other","args":{"count":1}}""", ErrorCodes.UnknownTarget, "Available targets: DemoRepo, Docs")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping","target":"DemoRepo"}""", ErrorCodes.InvalidRequest, "does not take a target")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping","args":["hi"]}""", ErrorCodes.InvalidArgs, "args must be a JSON object, not an array. Accepted args: echo (string, optional)")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping","args":{"other":1}}""", ErrorCodes.InvalidArgs, "args.other is not an accepted arg")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping","args":{"Echo":"hi"}}""", ErrorCodes.InvalidArgs, "args.Echo is not an accepted arg")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping","args":{"echo":5}}""", ErrorCodes.InvalidArgs, "args.echo must be a string, not a number")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping","args":{"echo":null}}""", ErrorCodes.InvalidArgs, "omit optional args instead of sending null")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping","args":{"echo":"a","echo":"b"}}""", ErrorCodes.InvalidArgs, "appears more than once")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"test.files","target":"DemoRepo"}""", ErrorCodes.InvalidArgs, "args.count is required")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"test.files","target":"DemoRepo","args":{"count":1.5}}""", ErrorCodes.InvalidArgs, "args.count must be a whole number")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"test.files","target":"DemoRepo","args":{"count":"3"}}""", ErrorCodes.InvalidArgs, "args.count must be an integer, not a string")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"test.files","target":"DemoRepo","args":{"count":1,"name":"abc1"}}""", ErrorCodes.InvalidArgs, "args.name must match the pattern [a-z]+")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"test.files","target":"DemoRepo","args":{"count":1,"name":"abc\n"}}""", ErrorCodes.InvalidArgs, "args.name must match the pattern [a-z]+")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"test.files","target":"DemoRepo","args":{"count":1,"flag":"yes"}}""", ErrorCodes.InvalidArgs, "args.flag must be a boolean, not a string")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"test.files","target":"DemoRepo","args":{"count":1,"tags":{}}}""", ErrorCodes.InvalidArgs, "args.tags must be an array, not an object")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"test.files","target":"DemoRepo","args":{"other":1}}""", ErrorCodes.InvalidArgs, "Accepted args: count (integer, required), filter (object, optional), flag (boolean, optional), name (string, optional, pattern [a-z]+), ratio (number, optional), tags (array, optional).")]
    public async Task Rejects_invalid_requests_with_a_code_and_an_actionable_message(string json, string code, string expectedText)
    {
        var ping = Ping();
        var files = TestFiles();
        using var h = new PipelineHarness([ping, files], WithTargets);
        h.Box.AddRequest("r1.json", json, Start);

        await h.ProcessInboxAsync();

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Rejected, response.Status);
        Assert.Equal(code, response.Error!.Code);
        Assert.Contains(expectedText, response.Error.Message);
        Assert.EndsWith("Send a corrected request with a new requestId.", response.Error.Message);
        Assert.DoesNotContain(h.Root.Path, response.Error.Message);
        Assert.Equal(FakeCatalogProvider.Version, response.CatalogVersion);
        Assert.Equal(0, ping.Executions + files.Executions);
        Assert.True(h.Jobs.IsPublished("r1"));
        Assert.Equal(["r1.json"], h.Box.Archived);
    }

    [Fact]
    public async Task Passes_validated_args_and_the_resolved_target_to_the_capability()
    {
        var files = TestFiles();
        using var h = new PipelineHarness([files], WithTargets);
        h.Box.AddRequest("r1.json", """
            {"schemaVersion":1,"requestId":"r1","capability":"TEST.FILES","target":"demorepo",
             "args":{"count":5.0,"name":"abc","flag":true,"ratio":0.5,"tags":["a"],"filter":{"x":1}}}
            """, Start);

        await h.ProcessInboxAsync();

        Assert.Equal(JobStatus.Succeeded, h.Box.Responses["r1"].Status);
        Assert.Equal("test.files", h.Box.Responses["r1"].Capability);
        var context = files.LastContext!;
        Assert.Equal("DemoRepo", context.TargetName);
        Assert.Equal(h.Options.Targets["DemoRepo"].Path, context.TargetPath);
        Assert.Equal(5, context.Args.GetProperty("count").GetInt64());
        Assert.Equal("abc", context.Args.GetProperty("name").GetString());
        Assert.Equal("r1", context.Request.RequestId);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping"}""")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping","args":null}""")]
    [InlineData("""{"schemaVersion":1,"requestId":"r1","capability":"agent.ping","target":"","expiresAtUtc":"2026-10-31T09:00:01Z"}""")]
    public async Task Missing_args_become_an_empty_object(string json)
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        h.Box.AddRequest("r1.json", json, Start);

        await h.ProcessInboxAsync();

        Assert.Equal(JobStatus.Succeeded, h.Box.Responses["r1"].Status);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, ping.LastContext!.Args.ValueKind);
        Assert.Empty(ping.LastContext.Args.EnumerateObject());
        Assert.Null(ping.LastContext.TargetName);
    }

    [Fact]
    public async Task Capability_failure_becomes_failed_with_its_error()
    {
        var ping = Ping((_, _) => Task.FromResult(CapabilityResult.Failure("backlog_unavailable", "The backlog could not be reached.")));
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");

        await h.ProcessInboxAsync();

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Failed, response.Status);
        Assert.Equal(new ErrorInfo("backlog_unavailable", "The backlog could not be reached."), response.Error);
    }

    [Fact]
    public async Task Capability_failure_without_an_error_uses_execution_failed()
    {
        var ping = Ping((_, _) => Task.FromResult(new CapabilityResult { Succeeded = false }));
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");

        await h.ProcessInboxAsync();

        Assert.Equal(ErrorCodes.ExecutionFailed, h.Box.Responses["r1"].Error!.Code);
    }

    [Fact]
    public async Task Capability_that_throws_becomes_internal_error_without_exception_details()
    {
        var ping = Ping((context, _) => context.Request.RequestId == "r1"
            ? throw new InvalidOperationException(@"Secret failure in C:\Users\me\secret.txt")
            : Task.FromResult(CapabilityResult.Success(null)));
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");
        h.AddRequest("r2");

        await h.ProcessInboxAsync();

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Failed, response.Status);
        Assert.Equal(ErrorCodes.InternalError, response.Error!.Code);
        Assert.DoesNotContain("Secret", response.Error.Message);
        Assert.DoesNotContain("InvalidOperationException", response.Error.Message);
        Assert.True(h.Jobs.IsPublished("r1"));
        Assert.Equal(JobStatus.Succeeded, h.Box.Responses["r2"].Status);
        Assert.Equal(["r1.json", "r2.json"], h.Box.Archived);
        var error = Assert.Single(h.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task Timeout_becomes_timed_out()
    {
        var ping = Ping(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return CapabilityResult.Success(null);
        }, timeout: TimeSpan.FromSeconds(10));
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");

        var pass = h.ProcessInboxAsync();
        await ping.Started.Task;
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await pass;

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.TimedOut, response.Status);
        Assert.Equal(ErrorCodes.Timeout, response.Error!.Code);
        Assert.Contains("within 10 seconds", response.Error.Message);
        Assert.Equal(Start.AddSeconds(10), response.CompletedAtUtc);
        Assert.Equal(["r1.json"], h.Box.Archived);
    }

    [Fact]
    public async Task Timeout_also_stops_waiting_for_a_capability_that_ignores_its_token()
    {
        var never = new TaskCompletionSource<CapabilityResult>();
        var ping = Ping((_, _) => never.Task, timeout: TimeSpan.FromSeconds(5));
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");

        var pass = h.ProcessInboxAsync();
        await ping.Started.Task;
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await pass;

        Assert.Equal(JobStatus.TimedOut, h.Box.Responses["r1"].Status);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("did not stop"));
    }

    [Fact]
    public async Task Shutdown_during_execution_becomes_interrupted_and_is_still_published()
    {
        var ping = Ping(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return CapabilityResult.Success(null);
        });
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");
        using var shutdown = new CancellationTokenSource();

        var pass = h.Processor.ProcessInboxAsync(shutdown.Token);
        await ping.Started.Task;
        shutdown.Cancel();
        await pass;

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Interrupted, response.Status);
        Assert.Equal(ErrorCodes.Interrupted, response.Error!.Code);
        Assert.Contains("not re-run", response.Error.Message);
        Assert.Same(response, h.Jobs.ResponseOf("r1"));
        Assert.True(h.Jobs.IsPublished("r1"));
        Assert.Equal(["r1.json"], h.Box.Archived);
    }

    [Fact]
    public async Task One_failing_item_does_not_block_the_next()
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");
        h.AddRequest("r2");
        h.Box.ReadFailures["r1.json"] = new UnauthorizedAccessException("denied");

        await h.ProcessInboxAsync();

        Assert.Equal(["r1.json"], h.Box.InboxFileNames);
        Assert.Equal(JobStatus.Succeeded, h.Box.Responses["r2"].Status);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("r1.json"));
    }

    [Fact]
    public async Task Publish_failure_is_finished_on_the_next_poll_without_running_again()
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");
        h.Box.PublishFailuresRemaining = 1;

        await h.ProcessInboxAsync();

        Assert.Equal(["r1.json"], h.Box.InboxFileNames);
        Assert.NotNull(h.Jobs.ResponseOf("r1"));
        Assert.False(h.Jobs.IsPublished("r1"));

        await h.ProcessInboxAsync();

        Assert.Equal(JobStatus.Succeeded, h.Box.Responses["r1"].Status);
        Assert.True(h.Jobs.IsPublished("r1"));
        Assert.Equal(["r1.json"], h.Box.Archived);
        Assert.Equal(1, ping.Executions);
    }

    [Fact]
    public async Task Job_claimed_without_a_response_is_reported_as_interrupted_on_the_next_poll()
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        h.AddRequest("r1");
        h.Jobs.SaveFailuresRemaining = 1;

        await h.ProcessInboxAsync();
        await h.ProcessInboxAsync();

        Assert.Equal(JobStatus.Interrupted, h.Box.Responses["r1"].Status);
        Assert.True(h.Jobs.IsPublished("r1"));
        Assert.Equal(["r1.json"], h.Box.Archived);
        Assert.Equal(1, ping.Executions);
    }

    [Fact]
    public async Task Recovery_reports_a_job_claimed_without_a_response_as_interrupted_and_never_runs_it()
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        var claimedAt = Start.AddMinutes(-3);
        h.Jobs.Seed("r1", RequestJson("r1", configure: j => j["correlationId"] = "c1"), response: null, published: false, claimedAt);

        await h.Processor.RecoverAsync(CancellationToken.None);

        var response = h.Box.Responses["r1"];
        Assert.Equal(JobStatus.Interrupted, response.Status);
        Assert.Equal(ErrorCodes.Interrupted, response.Error!.Code);
        Assert.Contains("new requestId", response.Error.Message);
        Assert.Equal("agent.ping", response.Capability);
        Assert.Equal("c1", response.CorrelationId);
        Assert.Equal(claimedAt, response.StartedAtUtc);
        Assert.Equal(Start, response.CompletedAtUtc);
        Assert.Equal(FakeCatalogProvider.Version, response.CatalogVersion);
        Assert.Same(response, h.Jobs.ResponseOf("r1"));
        Assert.True(h.Jobs.IsPublished("r1"));
        Assert.Equal(0, ping.Executions);
    }

    [Fact]
    public async Task Recovery_without_a_readable_request_still_reports_interrupted()
    {
        using var h = new PipelineHarness(Ping());
        h.Jobs.Seed("r1", RequestJson("r1"), response: null, published: false, Start);
        File.Delete(Path.Combine(h.Jobs.GetJobDirectory("r1"), "request.json"));

        await h.Processor.RecoverAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Interrupted, h.Box.Responses["r1"].Status);
        Assert.Null(h.Box.Responses["r1"].Capability);
    }

    [Fact]
    public async Task Recovery_publishes_a_saved_response_that_was_not_published()
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        var saved = new AgentResponse { RequestId = "r1", Status = JobStatus.Succeeded, CompletedAtUtc = Start };
        h.Jobs.Seed("r1", RequestJson("r1"), saved, published: false, Start);

        await h.Processor.RecoverAsync(CancellationToken.None);

        Assert.Same(saved, h.Box.Responses["r1"]);
        Assert.True(h.Jobs.IsPublished("r1"));
        Assert.Equal(0, ping.Executions);
    }

    [Fact]
    public async Task Recovery_continues_after_a_job_that_cannot_be_recovered()
    {
        using var h = new PipelineHarness(Ping());
        h.Jobs.Seed("r1", RequestJson("r1"), response: null, published: false, Start);
        h.Jobs.Seed("r2", RequestJson("r2"), response: null, published: false, Start);
        h.Box.PublishFailuresRemaining = 1;

        await h.Processor.RecoverAsync(CancellationToken.None);

        Assert.Single(h.Box.Responses);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Could not recover"));
    }

    [Fact]
    public async Task Execute_runs_a_request_without_any_job_or_box_state()
    {
        var ping = Ping();
        using var h = new PipelineHarness(ping);
        var request = new AgentRequest { SchemaVersion = 1, RequestId = "local-1", Capability = "agent.ping" };

        var response = await h.Processor.ExecuteAsync(request, h.Root.Path, CancellationToken.None);

        Assert.Equal(JobStatus.Succeeded, response.Status);
        Assert.Equal(h.Root.Path, ping.LastContext!.JobDirectory);
        Assert.Equal(0, h.Jobs.Count);
        Assert.Empty(h.Box.Responses);
    }

    private static void WithTargets(AgentBridgeOptions options)
    {
        options.Targets["DemoRepo"] = new TargetOptions { Path = Path.GetTempPath(), Description = "Demo repository" };
        options.Targets["Docs"] = new TargetOptions { Path = Path.GetTempPath(), Description = "Documentation" };
    }

    private static FakeCapability TestFiles() => new(new CapabilityDescriptor
    {
        Name = "test.files",
        Description = "Lists files of a target.",
        RequiresTarget = true,
        Parameters = new Dictionary<string, ParameterDescriptor>
        {
            ["count"] = new() { Type = ParameterTypes.Integer, Required = true },
            ["name"] = new() { Type = ParameterTypes.String, Pattern = "[a-z]+" },
            ["flag"] = new() { Type = ParameterTypes.Boolean },
            ["ratio"] = new() { Type = ParameterTypes.Number },
            ["tags"] = new() { Type = ParameterTypes.Array },
            ["filter"] = new() { Type = ParameterTypes.Object },
        },
    });
}
