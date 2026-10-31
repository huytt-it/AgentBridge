namespace AgentBridge.Hosting;

/// <summary>Holds an exclusive lock file for the process lifetime so two workers never share one data root.</summary>
internal sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    /// <summary>Returns null when another process holds the lock.</summary>
    public static InstanceLock? TryAcquire(string lockFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        try
        {
            return new InstanceLock(new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
