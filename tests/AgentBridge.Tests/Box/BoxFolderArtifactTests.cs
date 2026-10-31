using AgentBridge.Box;
using AgentBridge.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Tests.Box;

public sealed class BoxFolderArtifactTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AgentBridgePaths _paths;
    private readonly BoxFolder _box;
    private readonly string _source;

    public BoxFolderArtifactTests()
    {
        _paths = TestEnvironment.CreatePaths(TestEnvironment.CreateOptions(_temp));
        _box = new BoxFolder(_paths, NullLogger<BoxFolder>.Instance);
        _box.EnsureLayout();
        _source = _temp.Combine("job", "artifacts");
        Directory.CreateDirectory(_source);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Copies_files_and_subfolders_and_returns_box_relative_paths()
    {
        File.WriteAllText(Path.Combine(_source, "result.md"), "# Backlog");
        Directory.CreateDirectory(Path.Combine(_source, "data"));
        File.WriteAllText(Path.Combine(_source, "data", "rows.csv"), "a,b");

        var artifacts = await _box.PublishArtifactsAsync("r1", _source, CancellationToken.None);

        Assert.Equal(["artifacts/r1/data/rows.csv", "artifacts/r1/result.md"], artifacts.Select(a => a.Path));
        Assert.Equal(["text/csv", "text/markdown"], artifacts.Select(a => a.ContentType));
        Assert.Equal([3L, 9L], artifacts.Select(a => a.SizeBytes));
        Assert.Equal("# Backlog", File.ReadAllText(Path.Combine(_paths.Artifacts, "r1", "result.md")));
        Assert.Equal("a,b", File.ReadAllText(Path.Combine(_paths.Artifacts, "r1", "data", "rows.csv")));
    }

    [Fact]
    public async Task Publishing_again_replaces_earlier_copies()
    {
        File.WriteAllText(Path.Combine(_source, "result.md"), "old");
        await _box.PublishArtifactsAsync("r1", _source, CancellationToken.None);
        File.WriteAllText(Path.Combine(_source, "result.md"), "new");

        await _box.PublishArtifactsAsync("r1", _source, CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_paths.Artifacts, "r1", "result.md")));
    }

    [Fact]
    public async Task A_missing_source_folder_publishes_nothing()
    {
        var artifacts = await _box.PublishArtifactsAsync("r1", _temp.Combine("nope"), CancellationToken.None);

        Assert.Empty(artifacts);
        Assert.False(Directory.Exists(Path.Combine(_paths.Artifacts, "r1")));
    }

    [Fact]
    public async Task Leaves_no_temp_files_inside_the_box_folder()
    {
        File.WriteAllText(Path.Combine(_source, "result.md"), "# Backlog");

        await _box.PublishArtifactsAsync("r1", _source, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(_paths.BoxRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Rejects_unsafe_request_ids()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _box.PublishArtifactsAsync("../escape", _source, CancellationToken.None));
    }
}
