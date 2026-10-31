using System.Text;
using AgentBridge.Contracts;
using AgentBridge.Jobs;

namespace AgentBridge.Tests.Processing.Fakes;

/// <summary>In-memory job store. Claiming writes request.json into a real temp folder, as recovery reads it from there.</summary>
internal sealed class FakeJobStore : IJobStore
{
    private readonly object _gate = new();
    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public FakeJobStore(string root, TimeProvider time)
    {
        _root = root;
        _time = time;
    }

    public int EnsureLayoutCalls { get; private set; }
    public int SaveFailuresRemaining { get; set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Count;
            }
        }
    }

    public AgentResponse? ResponseOf(string requestId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(requestId, out var job) ? job.Response : null;
        }
    }

    public bool IsPublished(string requestId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(requestId, out var job) && job.Published;
        }
    }

    /// <summary>A job left behind by an earlier run.</summary>
    public void Seed(string requestId, string requestJson, AgentResponse? response, bool published, DateTimeOffset claimedAtUtc)
    {
        var bytes = Encoding.UTF8.GetBytes(requestJson);
        WriteRequestFile(requestId, bytes);
        lock (_gate)
        {
            _jobs[requestId] = new Job(bytes, Hashing.Sha256Hex(bytes), claimedAtUtc) { Response = response, Published = published };
        }
    }

    public void EnsureLayout()
    {
        lock (_gate)
        {
            EnsureLayoutCalls++;
        }
    }

    public Task<bool> TryClaimAsync(string requestId, byte[] rawRequest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_jobs.ContainsKey(requestId))
            {
                return Task.FromResult(false);
            }
            WriteRequestFile(requestId, rawRequest);
            _jobs[requestId] = new Job(rawRequest, Hashing.Sha256Hex(rawRequest), _time.GetUtcNow());
            return Task.FromResult(true);
        }
    }

    public JobRecord? Find(string requestId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(requestId, out var job) ? ToRecord(requestId, job) : null;
        }
    }

    public Task SaveResponseAsync(AgentResponse response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (SaveFailuresRemaining > 0)
            {
                SaveFailuresRemaining--;
                throw new IOException("Disk full.");
            }
            _jobs[response.RequestId].Response = response;
        }
        return Task.CompletedTask;
    }

    public Task<AgentResponse?> ReadResponseAsync(string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ResponseOf(requestId));
    }

    public void MarkPublished(string requestId)
    {
        lock (_gate)
        {
            var job = _jobs[requestId];
            if (job.Response is null)
            {
                throw new InvalidOperationException($"{requestId} has no saved response to mark as published.");
            }
            job.Published = true;
        }
    }

    public IReadOnlyList<JobRecord> ListUnfinished()
    {
        lock (_gate)
        {
            return _jobs
                .Where(j => j.Value.Response is null || !j.Value.Published)
                .Select(j => ToRecord(j.Key, j.Value))
                .ToList();
        }
    }

    public string GetJobDirectory(string requestId) => Path.Combine(_root, requestId);

    private void WriteRequestFile(string requestId, byte[] bytes)
    {
        Directory.CreateDirectory(GetJobDirectory(requestId));
        File.WriteAllBytes(Path.Combine(GetJobDirectory(requestId), "request.json"), bytes);
    }

    private JobRecord ToRecord(string requestId, Job job) =>
        new(requestId, GetJobDirectory(requestId), job.Sha256, job.ClaimedAtUtc, job.Response is not null, job.Published);

    private sealed class Job(byte[] request, string sha256, DateTimeOffset claimedAtUtc)
    {
        public byte[] Request { get; } = request;
        public string Sha256 { get; } = sha256;
        public DateTimeOffset ClaimedAtUtc { get; } = claimedAtUtc;
        public AgentResponse? Response { get; set; }
        public bool Published { get; set; }
    }
}
