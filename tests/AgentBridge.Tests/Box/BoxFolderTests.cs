using System.Text;
using System.Text.Json;
using AgentBridge.Box;
using AgentBridge.Contracts;
using AgentBridge.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentBridge.Tests.Box;

public sealed class BoxFolderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 30, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly AgentBridgePaths _paths;
    private readonly BoxFolder _box;

    public BoxFolderTests()
    {
        _paths = TestEnvironment.CreatePaths(TestEnvironment.CreateOptions(_temp));
        _box = new BoxFolder(_paths, NullLogger<BoxFolder>.Instance, new FakeTimeProvider(Now));
        _box.EnsureLayout();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EnsureLayout_creates_every_folder_and_can_run_again()
    {
        Directory.Delete(_paths.BoxRoot, recursive: true);

        _box.EnsureLayout();
        _box.EnsureLayout();

        foreach (var folder in new[] { _paths.Inbox, _paths.Responses, _paths.Artifacts, _paths.Archive, _paths.DeadLetter })
        {
            Assert.True(Directory.Exists(folder), folder);
        }
    }

    [Fact]
    public void ListInbox_returns_only_top_level_json_files()
    {
        WriteInboxFiles("a.json", "B.JSON", "c.Json", "notes.txt", "noextension");
        WriteInbox("sub/nested.json");

        var names = _box.ListInbox().Select(item => item.FileName).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["B.JSON", "a.json", "c.Json"], names);
    }

    [Fact]
    public void ListInbox_skips_hidden_and_temporary_files()
    {
        WriteInboxFiles("ok.json", ".hidden.json", "~lock.json", "upload.json.tmp", "upload.json.part", "upload.json.partial", "x.TMP");
        if (OperatingSystem.IsWindows())
        {
            var hidden = WriteInbox("attribute.json");
            File.SetAttributes(hidden, FileAttributes.Hidden);
        }

        var names = _box.ListInbox().Select(item => item.FileName).ToArray();

        Assert.Equal(["ok.json"], names);
    }

    [Fact]
    public void ListInbox_orders_by_write_time_then_name()
    {
        var older = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var newer = older.AddMinutes(5);
        SetWriteTime(WriteInbox("z-oldest.json"), older);
        SetWriteTime(WriteInbox("d.json"), newer);
        SetWriteTime(WriteInbox("c.json"), newer);
        SetWriteTime(WriteInbox("a-newest.json"), newer.AddMinutes(1));

        var names = _box.ListInbox().Select(item => item.FileName).ToArray();

        Assert.Equal(["z-oldest.json", "c.json", "d.json", "a-newest.json"], names);
    }

    [Fact]
    public void ListInbox_describes_each_file()
    {
        var path = WriteInbox("a.json", "12345");
        var written = new DateTime(2026, 10, 30, 7, 15, 0, DateTimeKind.Utc);
        SetWriteTime(path, written);

        var item = Assert.Single(_box.ListInbox());

        Assert.Equal(path, item.FullPath);
        Assert.Equal(5, item.Length);
        Assert.Equal(new DateTimeOffset(written), item.LastWriteTimeUtc);
        Assert.Equal(TimeSpan.Zero, item.LastWriteTimeUtc.Offset);
    }

    [Fact]
    public void ListInbox_is_empty_when_the_inbox_does_not_exist()
    {
        Directory.Delete(_paths.Inbox);

        Assert.Empty(_box.ListInbox());
    }

    [Fact]
    public void ListInbox_still_lists_a_file_that_someone_holds_open()
    {
        var path = WriteInbox("busy.json");
        using var lockedByWriter = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var item = Assert.Single(_box.ListInbox());

        Assert.Equal("busy.json", item.FileName);
    }

    [Fact]
    public async Task ReadAsync_returns_the_exact_bytes()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}', 0x00, 0xFF];
        File.WriteAllBytes(Path.Combine(_paths.Inbox, "a.json"), bytes);
        var item = Assert.Single(_box.ListInbox());

        var read = await _box.ReadAsync(item, CancellationToken.None);

        Assert.Equal(bytes, read);
    }

    [Fact]
    public async Task ReadAsync_does_not_block_a_writer_that_still_has_the_file_open()
    {
        var path = WriteInbox("syncing.json", "{}");
        var item = Assert.Single(_box.ListInbox());
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var read = await _box.ReadAsync(item, CancellationToken.None);

        Assert.Equal("{}", Encoding.UTF8.GetString(read));
    }

    [Fact]
    public async Task ReadAsync_throws_an_IOException_when_the_file_was_deleted_after_listing()
    {
        var path = WriteInbox("a.json");
        var item = Assert.Single(_box.ListInbox());
        File.Delete(path);

        await Assert.ThrowsAnyAsync<IOException>(() => _box.ReadAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveAsync_moves_the_file_and_keeps_its_name()
    {
        var path = WriteInbox("req.json", "{\"a\":1}");
        var item = Assert.Single(_box.ListInbox());

        await _box.ArchiveAsync(item, CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(_paths.Archive, "req.json")));
    }

    [Fact]
    public async Task ArchiveAsync_adds_a_numbered_suffix_instead_of_overwriting()
    {
        File.WriteAllText(Path.Combine(_paths.Archive, "req.json"), "first");
        foreach (var content in new[] { "second", "third" })
        {
            WriteInbox("req.json", content);
            await _box.ArchiveAsync(Assert.Single(_box.ListInbox()), CancellationToken.None);
        }

        Assert.Equal("first", File.ReadAllText(Path.Combine(_paths.Archive, "req.json")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(_paths.Archive, "req-1.json")));
        Assert.Equal("third", File.ReadAllText(Path.Combine(_paths.Archive, "req-2.json")));
        Assert.Empty(_box.ListInbox());
    }

    [Fact]
    public async Task ArchiveAsync_skips_a_suffix_that_is_taken_too()
    {
        File.WriteAllText(Path.Combine(_paths.Archive, "req.json"), "first");
        File.WriteAllText(Path.Combine(_paths.Archive, "req-1.json"), "second");
        WriteInbox("req.json", "third");

        await _box.ArchiveAsync(Assert.Single(_box.ListInbox()), CancellationToken.None);

        Assert.Equal("third", File.ReadAllText(Path.Combine(_paths.Archive, "req-2.json")));
    }

    [Fact]
    public async Task ArchiveAsync_returns_quietly_when_the_file_is_already_gone()
    {
        var path = WriteInbox("req.json");
        var item = Assert.Single(_box.ListInbox());
        File.Delete(path);

        await _box.ArchiveAsync(item, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_paths.Archive));
    }

    [Fact]
    public async Task DeadLetterAsync_moves_the_file_and_writes_the_reason_beside_it()
    {
        var path = WriteInbox("bad.json", "not json");
        var item = Assert.Single(_box.ListInbox());

        await _box.DeadLetterAsync(item, "unreadable: expected '{'", CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Equal("not json", File.ReadAllText(Path.Combine(_paths.DeadLetter, "bad.json")));
        var reasonBytes = File.ReadAllBytes(Path.Combine(_paths.DeadLetter, "bad.json.reason.txt"));
        Assert.NotEqual(0xEF, reasonBytes[0]);
        var lines = Encoding.UTF8.GetString(reasonBytes).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["2026-10-31T09:30:00.0000000Z", "unreadable: expected '{'"], lines);
    }

    [Fact]
    public async Task DeadLetterAsync_numbers_the_file_and_its_reason_when_the_name_is_taken()
    {
        File.WriteAllText(Path.Combine(_paths.DeadLetter, "bad.json"), "old");
        File.WriteAllText(Path.Combine(_paths.DeadLetter, "bad.json.reason.txt"), "old reason");
        WriteInbox("bad.json", "new");

        await _box.DeadLetterAsync(Assert.Single(_box.ListInbox()), "second reason", CancellationToken.None);

        Assert.Equal("old", File.ReadAllText(Path.Combine(_paths.DeadLetter, "bad.json")));
        Assert.Equal("old reason", File.ReadAllText(Path.Combine(_paths.DeadLetter, "bad.json.reason.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_paths.DeadLetter, "bad-1.json")));
        Assert.Contains("second reason", File.ReadAllText(Path.Combine(_paths.DeadLetter, "bad-1.json.reason.txt")));
    }

    [Fact]
    public async Task DeadLetterAsync_returns_quietly_when_the_file_is_already_gone()
    {
        var path = WriteInbox("bad.json");
        var item = Assert.Single(_box.ListInbox());
        File.Delete(path);

        await _box.DeadLetterAsync(item, "whatever", CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_paths.DeadLetter));
    }

    [Fact]
    public async Task PublishResponseAsync_writes_utf8_json_named_after_the_request()
    {
        var response = NewResponse("req-1", summary: "héllo ✓");

        await _box.PublishResponseAsync(response, CancellationToken.None);

        var bytes = File.ReadAllBytes(Path.Combine(_paths.Responses, "req-1.json"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM expected");
        var read = JsonSerializer.Deserialize<AgentResponse>(bytes, JsonDefaults.Options)!;
        Assert.Equal("req-1", read.RequestId);
        Assert.Equal(JobStatus.Succeeded, read.Status);
        Assert.Equal("héllo ✓", read.Summary);
    }

    [Fact]
    public async Task PublishResponseAsync_replaces_an_earlier_copy_and_leaves_no_temp_files()
    {
        await _box.PublishResponseAsync(NewResponse("req-1", summary: "first"), CancellationToken.None);
        await _box.PublishResponseAsync(NewResponse("req-1", summary: "second"), CancellationToken.None);
        await _box.PublishResponseAsync(NewResponse("req-1", summary: "second"), CancellationToken.None);

        var read = JsonSerializer.Deserialize<AgentResponse>(File.ReadAllBytes(Path.Combine(_paths.Responses, "req-1.json")), JsonDefaults.Options)!;
        Assert.Equal("second", read.Summary);
        Assert.Equal([Path.Combine(_paths.Responses, "req-1.json")], AllFilesUnder(_paths.BoxRoot));
        Assert.Empty(AllFilesUnder(_paths.Staging));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("CON")]
    [InlineData("with space")]
    [InlineData(".hidden")]
    public async Task PublishResponseAsync_rejects_invalid_request_ids(string requestId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _box.PublishResponseAsync(NewResponse(requestId), CancellationToken.None));

        Assert.Empty(AllFilesUnder(_paths.BoxRoot));
        Assert.False(Directory.Exists(_paths.Staging));
    }

    [Fact]
    public async Task PublishCatalogAsync_writes_catalog_and_readme_at_the_box_root()
    {
        var catalog = new Catalog
        {
            CatalogVersion = "v1",
            GeneratedAtUtc = Now,
            Agent = new AgentInfo("test-agent", "0.1.0"),
            Capabilities = [new CatalogCapability { Name = "agent.ping", Description = "Checks the agent is alive." }],
        };

        await _box.PublishCatalogAsync(catalog, "# Readme ✓\n", CancellationToken.None);
        await _box.PublishCatalogAsync(catalog, "# Readme ✓\n", CancellationToken.None);

        var read = JsonSerializer.Deserialize<Catalog>(File.ReadAllBytes(_paths.CatalogFile), JsonDefaults.Options)!;
        Assert.Equal("v1", read.CatalogVersion);
        Assert.Equal("agent.ping", Assert.Single(read.Capabilities).Name);
        var readme = File.ReadAllBytes(_paths.ReadmeFile);
        Assert.Equal("# Readme ✓\n", Encoding.UTF8.GetString(readme));
        Assert.NotEqual(0xEF, readme[0]);
        Assert.Equal(
            new[] { _paths.CatalogFile, _paths.ReadmeFile }.Order(StringComparer.Ordinal),
            AllFilesUnder(_paths.BoxRoot).Order(StringComparer.Ordinal));
        Assert.Empty(AllFilesUnder(_paths.Staging));
    }

    [Fact]
    public async Task Staging_is_outside_the_box_folder()
    {
        await _box.PublishResponseAsync(NewResponse("req-1"), CancellationToken.None);

        Assert.True(Directory.Exists(_paths.Staging));
        Assert.False(AgentBridgePaths.IsSameOrInside(_paths.Staging, _paths.BoxRoot));
    }

    [Fact]
    public void The_container_builds_it_from_the_registered_services()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentBridge:BoxRoot"] = _paths.BoxRoot,
                ["AgentBridge:LocalDataRoot"] = _paths.LocalDataRoot,
            })
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddAgentBridge(configuration).BuildServiceProvider();

        Assert.IsType<BoxFolder>(provider.GetRequiredService<IBoxFolder>());
    }

    private string WriteInbox(string name, string content = "{}")
    {
        var path = Path.Combine(_paths.Inbox, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void WriteInboxFiles(params string[] names)
    {
        foreach (var name in names)
        {
            WriteInbox(name);
        }
    }

    private static void SetWriteTime(string path, DateTime utc) => File.SetLastWriteTimeUtc(path, utc);

    private static string[] AllFilesUnder(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];

    private static AgentResponse NewResponse(string requestId, string? summary = null) => new()
    {
        RequestId = requestId,
        Capability = "agent.ping",
        Status = JobStatus.Succeeded,
        CompletedAtUtc = Now,
        Agent = new AgentInfo("test-agent", "0.1.0"),
        CatalogVersion = "v1",
        Summary = summary,
    };
}
