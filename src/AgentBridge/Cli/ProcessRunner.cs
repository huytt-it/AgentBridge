using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgentBridge.Cli;

/// <summary>What to run. Arguments are passed one by one; there is no command-line string and no shell.</summary>
/// <param name="FileName">Full path of the executable.</param>
/// <param name="Environment">Variables added to (or replacing those of) the worker's own environment.</param>
/// <param name="StdoutLogPath">Receives the complete stdout. Created or overwritten.</param>
/// <param name="StderrLogPath">Receives the complete stderr. Created or overwritten.</param>
/// <param name="TailChars">How many trailing characters of each stream are kept in memory.</param>
internal sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string StdoutLogPath,
    string StderrLogPath,
    int TailChars);

internal sealed record ProcessRunResult(
    int ExitCode,
    string StdoutTail,
    string StderrTail,
    bool StdoutTruncated,
    bool StderrTruncated,
    TimeSpan Duration);

/// <summary>Runs a local executable safely: ArgumentList only, timeout and cancellation, whole process tree killed.</summary>
internal sealed class ProcessRunner
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PumpGrace = TimeSpan.FromSeconds(5);

    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts the process, closes its stdin at once and copies stdout and stderr to their log files as they arrive.
    /// When the token fires the whole process tree is killed, the exit is awaited and
    /// <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in request.Environment)
        {
            startInfo.Environment[name] = value;
        }

        // Opened first, so a bad log path fails before anything is started.
        await using var stdoutLog = new FileStream(request.StdoutLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var stderrLog = new FileStream(request.StderrLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new Win32Exception($"'{Path.GetFileName(request.FileName)}' did not start.");

        // A tool that waits for input must see end-of-file, not hang until the timeout.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The tool already exited and closed its end.
        }

        var stdout = PumpAsync(process.StandardOutput.BaseStream, stdoutLog, request.TailChars);
        var stderr = PumpAsync(process.StandardError.BaseStream, stderrLog, request.TailChars);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            // A grandchild that inherited the pipes can keep them open after the tool exited.
            await Task.WhenAll(stdout, stderr).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, request.FileName, stdout, stderr);
            throw;
        }

        stopwatch.Stop();
        var (stdoutTail, stdoutTruncated) = await stdout;
        var (stderrTail, stderrTruncated) = await stderr;
        return new ProcessRunResult(process.ExitCode, stdoutTail, stderrTail, stdoutTruncated, stderrTruncated, stopwatch.Elapsed);
    }

    private async Task StopAsync(Process process, string fileName, Task stdout, Task stderr)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Exception ex) when (ex is Win32Exception or AggregateException)
        {
            _logger.LogWarning(ex, "Could not kill the whole process tree of {Executable}; parts of it may still be running.",
                Path.GetFileName(fileName));
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(ExitGrace);
            await Task.WhenAll(stdout, stderr).WaitAsync(PumpGrace);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            _logger.LogWarning("{Executable} did not finish stopping within the grace period.", Path.GetFileName(fileName));
        }
    }

    /// <summary>
    /// Copies the bytes to the log as they arrive (so the log can be read while the tool still runs) and keeps the
    /// last <paramref name="tailChars"/> decoded characters.
    /// </summary>
    private static async Task<(string Tail, bool Truncated)> PumpAsync(Stream source, Stream log, int tailChars)
    {
        var tail = new TailBuffer(tailChars);
        var decoder = Utf8.GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[Utf8.GetMaxCharCount(bytes.Length)];
        try
        {
            int read;
            while ((read = await source.ReadAsync(bytes)) > 0)
            {
                await log.WriteAsync(bytes.AsMemory(0, read));
                await log.FlushAsync();
                tail.Append(chars.AsSpan(0, decoder.GetChars(bytes, 0, read, chars, 0)));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The streams were closed under us after a kill: what was read so far is what we have.
        }
        return tail.Snapshot();
    }

    private sealed class TailBuffer
    {
        private readonly StringBuilder _text = new();
        private readonly int _limit;
        private bool _truncated;

        public TailBuffer(int limit)
        {
            _limit = limit;
        }

        public void Append(ReadOnlySpan<char> chunk)
        {
            _text.Append(chunk);
            if (_text.Length > _limit * 2)
            {
                Trim();
            }
        }

        public (string Tail, bool Truncated) Snapshot()
        {
            if (_text.Length > _limit)
            {
                Trim();
            }
            var tail = _text.ToString();
            // Do not start in the middle of a surrogate pair.
            return (tail.Length > 0 && char.IsLowSurrogate(tail[0]) ? tail[1..] : tail, _truncated);
        }

        private void Trim()
        {
            _text.Remove(0, _text.Length - _limit);
            _truncated = true;
        }
    }
}
