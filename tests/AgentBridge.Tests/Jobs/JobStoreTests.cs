using System.Text;
using AgentBridge.Contracts;
using AgentBridge.Jobs;
using AgentBridge.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentBridge.Tests.Jobs;

public sealed class JobStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 9, 30, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly AgentBridgePaths _paths;
    private readonly FakeTimeProvider _time = new(Start);
    private readonly JobStore _store;

    public JobStoreTests()
    {
        _paths = TestEnvironment.CreatePaths(TestEnvironment.CreateOptions(_temp));
        _store = NewStore();
        _store.EnsureLayout();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EnsureLayout_creates_the_jobs_folder_and_can_run_again()
    {
        Directory.Delete(_paths.Jobs);

        _store.EnsureLayout();
        _store.EnsureLayout();

        Assert.True(Directory.Exists(_paths.Jobs));
    }

    [Fact]
    public void GetJobDirectory_is_a_folder_named_after_the_id_under_jobs()
    {
        Assert.Equal(Path.Combine(_paths.Jobs, "req-1"), _store.GetJobDirectory("req-1"));
    }

    [Fact]
    public async Task TryClaimAsync_stores_the_raw_bytes_exactly_as_received()
    {
        byte[] raw = [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'\r', (byte)'\n', (byte)'}', 0xFF];

        Assert.True(await _store.TryClaimAsync("req-1", raw, CancellationToken.None));

        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(_store.GetJobDirectory("req-1"), "request.json")));
    }

    [Fact]
    public async Task TryClaimAsync_twice_returns_false_and_keeps_the_first_request()
    {
        var first = Bytes("""{"requestId":"req-1","n":1}""");
        Assert.True(await _store.TryClaimAsync("req-1", first, CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(await _store.TryClaimAsync("req-1", Bytes("""{"requestId":"req-1","n":2}"""), CancellationToken.None));

        Assert.Equal(first, File.ReadAllBytes(Path.Combine(_store.GetJobDirectory("req-1"), "request.json")));
        var job = _store.Find("req-1")!;
        Assert.Equal(Hashing.Sha256Hex(first), job.RequestSha256);
        Assert.Equal(Start, job.ClaimedAtUtc);
    }

    [Fact]
    public async Task TryClaimAsync_treats_ids_that_differ_only_by_case_as_one_job_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // file names are case-sensitive elsewhere, so these are two jobs
        }

        Assert.True(await _store.TryClaimAsync("Req-1", Bytes("one"), CancellationToken.None));

        Assert.False(await _store.TryClaimAsync("req-1", Bytes("two"), CancellationToken.None));
        Assert.Equal(Hashing.Sha256Hex(Bytes("one")), _store.Find("req-1")!.RequestSha256);
    }

    [Fact]
    public async Task TryClaimAsync_leaves_no_temp_files_behind()
    {
        await _store.TryClaimAsync("req-1", Bytes("{}"), CancellationToken.None);

        var names = Directory.GetFiles(_store.GetJobDirectory("req-1")).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "meta.json", "request.json" }, names);
    }

    [Fact]
    public async Task Find_returns_null_for_an_id_that_was_never_claimed()
    {
        await _store.TryClaimAsync("other", Bytes("{}"), CancellationToken.None);

        Assert.Null(_store.Find("req-1"));
    }

    [Fact]
    public async Task Find_returns_null_for_a_folder_without_request_json_and_the_id_can_still_be_claimed()
    {
        var directory = _store.GetJobDirectory("req-1");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "meta.json"), """{"sha256":"abc","claimedAtUtc":"2026-01-01T00:00:00Z"}""");

        Assert.Null(_store.Find("req-1"));
        Assert.True(await _store.TryClaimAsync("req-1", Bytes("{}"), CancellationToken.None));
        Assert.Equal(Hashing.Sha256Hex(Bytes("{}")), _store.Find("req-1")!.RequestSha256);
    }

    [Fact]
    public async Task Find_describes_a_claimed_job()
    {
        var raw = Bytes("""{"requestId":"req-1"}""");
        await _store.TryClaimAsync("req-1", raw, CancellationToken.None);

        var job = _store.Find("req-1")!;

        Assert.Equal("req-1", job.RequestId);
        Assert.Equal(_store.GetJobDirectory("req-1"), job.Directory);
        Assert.Equal(Hashing.Sha256Hex(raw), job.RequestSha256);
        Assert.Equal(Start, job.ClaimedAtUtc);
        Assert.False(job.HasResponse);
        Assert.False(job.IsPublished);
    }

    [Fact]
    public async Task Find_derives_the_values_from_request_json_when_meta_json_is_missing_or_broken()
    {
        var raw = Bytes("""{"requestId":"req-1"}""");
        await _store.TryClaimAsync("req-1", raw, CancellationToken.None);
        var meta = Path.Combine(_store.GetJobDirectory("req-1"), "meta.json");
        var written = new DateTime(2026, 10, 30, 7, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(_store.GetJobDirectory("req-1"), "request.json"), written);

        File.Delete(meta);
        var withoutMeta = _store.Find("req-1")!;
        File.WriteAllText(meta, "{ not json");
        var brokenMeta = _store.Find("req-1")!;

        foreach (var job in new[] { withoutMeta, brokenMeta })
        {
            Assert.Equal(Hashing.Sha256Hex(raw), job.RequestSha256);
            Assert.Equal(new DateTimeOffset(written), job.ClaimedAtUtc);
        }
    }

    [Fact]
    public async Task Invalid_request_ids_are_rejected_everywhere()
    {
        foreach (var id in new[] { "", "../escape", "a/b", "a\\b", "CON", "with space", ".hidden", "trailing-newline\n" })
        {
            Assert.Throws<ArgumentException>(() => _store.GetJobDirectory(id));
            Assert.Throws<ArgumentException>(() => _store.Find(id));
            Assert.Throws<ArgumentException>(() => _store.MarkPublished(id));
            await Assert.ThrowsAsync<ArgumentException>(() => _store.TryClaimAsync(id, Bytes("{}"), CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => _store.ReadResponseAsync(id, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveResponseAsync(NewResponse(id), CancellationToken.None));
        }

        Assert.Empty(Directory.GetFileSystemEntries(_paths.Jobs));
    }

    [Fact]
    public async Task SaveResponseAsync_requires_a_claimed_job()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveResponseAsync(NewResponse("req-1"), CancellationToken.None));

        Assert.False(Directory.Exists(_store.GetJobDirectory("req-1")));
    }

    [Fact]
    public async Task SaveResponseAsync_then_ReadResponseAsync_round_trips_the_response()
    {
        await _store.TryClaimAsync("req-1", Bytes("{}"), CancellationToken.None);

        await _store.SaveResponseAsync(NewResponse("req-1", "héllo ✓"), CancellationToken.None);

        var read = await _store.ReadResponseAsync("req-1", CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal("req-1", read.RequestId);
        Assert.Equal(JobStatus.Succeeded, read.Status);
        Assert.Equal("héllo ✓", read.Summary);
        Assert.Equal(Start, read.CompletedAtUtc);
        Assert.True(_store.Find("req-1")!.HasResponse);
    }

    [Fact]
    public async Task SaveResponseAsync_replaces_the_earlier_response_without_a_bom_or_temp_files()
    {
        await _store.TryClaimAsync("req-1", Bytes("{}"), CancellationToken.None);
        await _store.SaveResponseAsync(NewResponse("req-1", "first"), CancellationToken.None);

        await _store.SaveResponseAsync(NewResponse("req-1", "second"), CancellationToken.None);

        Assert.Equal("second", (await _store.ReadResponseAsync("req-1", CancellationToken.None))!.Summary);
        var directory = _store.GetJobDirectory("req-1");
        var bytes = File.ReadAllBytes(Path.Combine(directory, "response.json"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM expected");
        var names = Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "meta.json", "request.json", "response.json" }, names);
    }

    [Fact]
    public async Task ReadResponseAsync_is_null_when_there_is_no_response()
    {
        Assert.Null(await _store.ReadResponseAsync("never-claimed", CancellationToken.None));

        await _store.TryClaimAsync("req-1", Bytes("{}"), CancellationToken.None);
        Assert.Null(await _store.ReadResponseAsync("req-1", CancellationToken.None));
    }

    [Fact]
    public async Task MarkPublished_writes_a_utc_timestamp_marker()
    {
        await _store.TryClaimAsync("req-1", Bytes("{}"), CancellationToken.None);
        await _store.SaveResponseAsync(NewResponse("req-1"), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(30));

        _store.MarkPublished("req-1");
        _store.MarkPublished("req-1");

        var marker = Path.Combine(_store.GetJobDirectory("req-1"), "published.marker");
        Assert.Equal("2026-10-31T09:30:30.0000000Z", File.ReadAllText(marker));
        var job = _store.Find("req-1")!;
        Assert.True(job.HasResponse);
        Assert.True(job.IsPublished);
    }

    [Fact]
    public void MarkPublished_requires_a_claimed_job()
    {
        Assert.Throws<InvalidOperationException>(() => _store.MarkPublished("req-1"));

        Assert.False(Directory.Exists(_store.GetJobDirectory("req-1")));
    }

    [Fact]
    public async Task ListUnfinished_covers_claimed_only_and_unpublished_responses_but_not_published_jobs()
    {
        await Claim("claimed-only");
        await Claim("response-saved");
        await _store.SaveResponseAsync(NewResponse("response-saved"), CancellationToken.None);
        await Claim("published");
        await _store.SaveResponseAsync(NewResponse("published"), CancellationToken.None);
        _store.MarkPublished("published");

        var unfinished = _store.ListUnfinished();

        Assert.Equal(["claimed-only", "response-saved"], unfinished.Select(job => job.RequestId).ToArray());
        Assert.False(unfinished[0].HasResponse);
        Assert.True(unfinished[1].HasResponse);
        Assert.False(unfinished[1].IsPublished);
    }

    [Fact]
    public async Task ListUnfinished_is_ordered_by_claim_time_not_by_name()
    {
        await Claim("b");
        await Claim("c");
        await Claim("a");

        Assert.Equal(["b", "c", "a"], _store.ListUnfinished().Select(job => job.RequestId).ToArray());
    }

    [Fact]
    public async Task ListUnfinished_ignores_folders_that_are_not_claimed_jobs()
    {
        await Claim("real");
        Directory.CreateDirectory(Path.Combine(_paths.Jobs, "no-request-file"));
        Directory.CreateDirectory(Path.Combine(_paths.Jobs, ".not-an-id"));
        File.WriteAllText(Path.Combine(_paths.Jobs, "stray.txt"), "x");

        Assert.Equal(["real"], _store.ListUnfinished().Select(job => job.RequestId).ToArray());
    }

    [Fact]
    public void ListUnfinished_is_empty_when_there_is_no_jobs_folder()
    {
        Directory.Delete(_paths.Jobs);

        Assert.Empty(_store.ListUnfinished());
    }

    [Fact]
    public async Task A_new_store_over_the_same_folder_sees_the_same_records()
    {
        var raw = Bytes("""{"requestId":"req-1"}""");
        await Claim("done");
        await _store.SaveResponseAsync(NewResponse("done"), CancellationToken.None);
        _store.MarkPublished("done");
        await Claim("saved");
        await _store.SaveResponseAsync(NewResponse("saved", "kept"), CancellationToken.None);
        await _store.TryClaimAsync("req-1", raw, CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(2));

        var restarted = NewStore();

        Assert.Equal(_store.Find("done"), restarted.Find("done"));
        Assert.Equal(_store.Find("saved"), restarted.Find("saved"));
        var job = restarted.Find("req-1")!;
        Assert.Equal(Hashing.Sha256Hex(raw), job.RequestSha256);
        Assert.Equal(Start.AddSeconds(2), job.ClaimedAtUtc);
        Assert.Equal(["saved", "req-1"], restarted.ListUnfinished().Select(unfinished => unfinished.RequestId).ToArray());
        Assert.Equal("kept", (await restarted.ReadResponseAsync("saved", CancellationToken.None))!.Summary);
        Assert.False(await restarted.TryClaimAsync("req-1", Bytes("other"), CancellationToken.None));
    }

    private JobStore NewStore() => new(_paths, _time, NullLogger<JobStore>.Instance);

    /// <summary>Claims one second after the previous claim, so claim times are distinct.</summary>
    private async Task Claim(string requestId)
    {
        Assert.True(await _store.TryClaimAsync(requestId, Bytes($$"""{"requestId":"{{requestId}}"}"""), CancellationToken.None));
        _time.Advance(TimeSpan.FromSeconds(1));
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private AgentResponse NewResponse(string requestId, string? summary = null) => new()
    {
        RequestId = requestId,
        Capability = "agent.ping",
        Status = JobStatus.Succeeded,
        CompletedAtUtc = Start,
        Agent = new AgentInfo("test-agent", "0.1.0"),
        CatalogVersion = "v1",
        Summary = summary,
    };
}
