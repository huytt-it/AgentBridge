using System.Text;
using AgentBridge.Box;
using AgentBridge.Contracts;

namespace AgentBridge.Tests.Processing.Fakes;

/// <summary>In-memory Box folder. Async methods honour cancellation like real file IO would.</summary>
internal sealed class FakeBoxFolder : IBoxFolder
{
    private readonly object _gate = new();
    private readonly List<(InboxItem Item, byte[] Bytes)> _inbox = [];
    private readonly List<(Func<bool> Condition, TaskCompletionSource Done)> _waiters = [];

    public int EnsureLayoutCalls { get; private set; }
    public int ListInboxCalls { get; private set; }
    public int PublishCount { get; private set; }
    public Dictionary<string, AgentResponse> Responses { get; } = [];
    public List<string> Archived { get; } = [];
    public Dictionary<string, string> DeadLettered { get; } = [];
    public Dictionary<string, IReadOnlyList<ArtifactRef>> PublishedArtifacts { get; } = [];
    public Dictionary<string, bool> ArtifactsPublishedBeforeResponse { get; } = [];
    public Catalog? PublishedCatalog { get; private set; }
    public string? PublishedReadme { get; private set; }

    public Exception? EnsureLayoutFailure { get; set; }
    public int ListInboxFailuresRemaining { get; set; }
    public int PublishFailuresRemaining { get; set; }

    /// <summary>ReadAsync throws IOException for these files, as for a file locked by the sync client.</summary>
    public HashSet<string> LockedFiles { get; } = [];

    /// <summary>ReadAsync throws this for the file: an unexpected failure.</summary>
    public Dictionary<string, Exception> ReadFailures { get; } = [];

    public IReadOnlyList<string> InboxFileNames
    {
        get
        {
            lock (_gate)
            {
                return _inbox.Select(f => f.Item.FileName).ToList();
            }
        }
    }

    public InboxItem AddRequest(string fileName, string json, DateTimeOffset lastWriteTimeUtc) =>
        AddFile(fileName, Encoding.UTF8.GetBytes(json), lastWriteTimeUtc);

    public InboxItem AddFile(string fileName, byte[] bytes, DateTimeOffset lastWriteTimeUtc)
    {
        var item = new InboxItem(Path.Combine("box", "inbox", fileName), fileName, bytes.Length, lastWriteTimeUtc);
        lock (_gate)
        {
            _inbox.Add((item, bytes));
        }
        return item;
    }

    /// <summary>Completes when the condition holds after a change; fails after 10 seconds so a broken test cannot hang.</summary>
    public Task WaitUntilAsync(Func<bool> condition)
    {
        lock (_gate)
        {
            if (condition())
            {
                return Task.CompletedTask;
            }
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((condition, done));
            return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public void EnsureLayout()
    {
        lock (_gate)
        {
            if (EnsureLayoutFailure is not null)
            {
                throw EnsureLayoutFailure;
            }
            EnsureLayoutCalls++;
        }
    }

    public IReadOnlyList<InboxItem> ListInbox()
    {
        lock (_gate)
        {
            ListInboxCalls++;
            Changed();
            if (ListInboxFailuresRemaining > 0)
            {
                ListInboxFailuresRemaining--;
                throw new IOException("Box folder unavailable.");
            }
            return _inbox.Select(f => f.Item).ToList();
        }
    }

    public Task<byte[]> ReadAsync(InboxItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (LockedFiles.Contains(item.FileName))
            {
                throw new IOException("The file is locked by another process.");
            }
            if (ReadFailures.TryGetValue(item.FileName, out var failure))
            {
                throw failure;
            }
            return Task.FromResult(Find(item).Bytes);
        }
    }

    public Task ArchiveAsync(InboxItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _inbox.Remove(Find(item));
            Archived.Add(item.FileName);
            Changed();
        }
        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(InboxItem item, string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _inbox.Remove(Find(item));
            DeadLettered[item.FileName] = reason;
            Changed();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ArtifactRef>> PublishArtifactsAsync(string requestId, string sourceDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var artifacts = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file => new ArtifactRef(
                $"artifacts/{requestId}/{Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/')}",
                "text/markdown",
                new FileInfo(file).Length))
            .ToList();
        lock (_gate)
        {
            PublishedArtifacts[requestId] = artifacts;
            ArtifactsPublishedBeforeResponse[requestId] = !Responses.ContainsKey(requestId);
            Changed();
        }
        return Task.FromResult<IReadOnlyList<ArtifactRef>>(artifacts);
    }

    public Task PublishResponseAsync(AgentResponse response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (PublishFailuresRemaining > 0)
            {
                PublishFailuresRemaining--;
                throw new IOException("Box folder unavailable.");
            }
            Responses[response.RequestId] = response;
            PublishCount++;
            Changed();
        }
        return Task.CompletedTask;
    }

    public Task PublishCatalogAsync(Catalog catalog, string readme, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            PublishedCatalog = catalog;
            PublishedReadme = readme;
            Changed();
        }
        return Task.CompletedTask;
    }

    private (InboxItem Item, byte[] Bytes) Find(InboxItem item)
    {
        var index = _inbox.FindIndex(f => f.Item.FileName == item.FileName);
        return index >= 0 ? _inbox[index] : throw new IOException($"{item.FileName} is gone.");
    }

    private void Changed()
    {
        for (var i = _waiters.Count - 1; i >= 0; i--)
        {
            if (_waiters[i].Condition())
            {
                _waiters[i].Done.TrySetResult();
                _waiters.RemoveAt(i);
            }
        }
    }
}
