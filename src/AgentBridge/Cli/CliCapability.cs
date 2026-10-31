using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;

namespace AgentBridge.Cli;

/// <summary>Everything the factory worked out and checked at startup for one cli entry.</summary>
internal sealed record CliCommand(
    string ExePath,
    string OutputFormat,
    bool RequiresTarget,
    string? WorkingDirectory,
    IReadOnlyList<ArgTemplate> Args,
    IReadOnlyList<(string Param, IReadOnlyList<ArgTemplate> Items)> OptionalArgs,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>A capability that runs a local command-line tool and turns its result into a response.</summary>
internal sealed class CliCapability : ICapability
{
    public const int MaxInlineJsonBytes = 256 * 1024;
    public const int MaxTextChars = 32 * 1024;
    private const int MaxErrorChars = 2000;
    private const string Masked = "***";
    private const int MinSecretLength = 6;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly CliCommand _command;
    private readonly ProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly string[] _secrets;
    private readonly bool _usesOutputFile;

    public CliCapability(CapabilityDescriptor descriptor, CliCommand command, ProcessRunner runner, ILogger logger)
    {
        Descriptor = descriptor;
        _command = command;
        _runner = runner;
        _logger = logger;
        _usesOutputFile = command.Args
            .Concat(command.OptionalArgs.SelectMany(o => o.Items))
            .Any(template => template.Placeholders.Contains(CliCapabilityFactory.OutputFilePlaceholder));
        // Longest first, so a secret that contains another one is masked as a whole.
        _secrets = command.Environment.Values
            .Where(value => value.Length >= MinSecretLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public CapabilityDescriptor Descriptor { get; }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken)
    {
        var name = Descriptor.Name;
        Directory.CreateDirectory(context.ArtifactDirectory);

        var extension = _command.OutputFormat switch
        {
            OutputFormats.Markdown => ".md",
            OutputFormats.Csv => ".csv",
            _ => null,
        };
        var outputFile = extension is null ? null : Path.Combine(context.ArtifactDirectory, "result" + extension);

        if (!TryBuildArguments(context, outputFile, out var arguments, out var argumentError))
        {
            return CapabilityResult.Failure(ErrorCodes.InvalidArgs, argumentError);
        }

        var workingDirectory = _command.RequiresTarget ? context.TargetPath! : _command.WorkingDirectory ?? context.JobDirectory;
        if (!Directory.Exists(workingDirectory))
        {
            return CapabilityResult.Failure(ErrorCodes.ExecutionFailed,
                $"The working folder of '{name}' does not exist on this machine. Ask the user to check the target or WorkingDirectory configuration.");
        }

        var stdoutLog = Path.Combine(context.JobDirectory, "stdout.log");
        var stderrLog = Path.Combine(context.JobDirectory, "stderr.log");
        ProcessRunResult run;
        try
        {
            run = await _runner.RunAsync(
                new ProcessRunRequest(_command.ExePath, arguments, workingDirectory, _command.Environment, stdoutLog, stderrLog, MaxTextChars),
                cancellationToken);
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "{Capability}: could not start the executable.", name);
            return CapabilityResult.Failure(ErrorCodes.ExecutionFailed,
                $"'{name}' could not be started on this machine (error {ex.NativeErrorCode}). Ask the user to check its Exe and WorkingDirectory configuration.");
        }

        _logger.LogInformation("{Capability}: the tool exited with code {ExitCode} after {DurationMs} ms.",
            name, run.ExitCode, (long)run.Duration.TotalMilliseconds);

        if (run.ExitCode != 0)
        {
            var text = run.StderrTail.Trim().Length > 0 ? run.StderrTail : run.StdoutTail;
            return CapabilityResult.Failure(ErrorCodes.ExecutionFailed,
                Mask($"'{name}' exited with code {run.ExitCode}: {LastChars(text.Trim(), MaxErrorChars, "no output")}"));
        }

        return _command.OutputFormat switch
        {
            OutputFormats.Markdown or OutputFormats.Csv => CompleteFile(context, outputFile!, stdoutLog),
            OutputFormats.Json => CompleteJson(context, stdoutLog),
            _ => CompleteText(run),
        };
    }

    private CapabilityResult CompleteFile(CapabilityContext context, string outputFile, string stdoutLog)
    {
        var fileName = Path.GetFileName(outputFile);
        if (_usesOutputFile)
        {
            if (!File.Exists(outputFile))
            {
                return CapabilityResult.Failure(ErrorCodes.ExecutionFailed,
                    $"'{Descriptor.Name}' finished, but the tool did not write its output file, so there is no result. "
                    + "Ask the user to check that the tool accepts the output file argument.");
            }
        }
        else
        {
            File.Copy(stdoutLog, outputFile, overwrite: true);
        }

        MaskArtifacts(context.ArtifactDirectory);
        return CapabilityResult.Success(null, $"Wrote {fileName} ({FormatSize(new FileInfo(outputFile).Length)}).");
    }

    private CapabilityResult CompleteJson(CapabilityContext context, string stdoutLog)
    {
        var text = Mask(File.ReadAllText(stdoutLog, Utf8));
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            return CapabilityResult.Failure(ErrorCodes.ExecutionFailed,
                $"'{Descriptor.Name}' finished, but its output is not valid JSON ({ex.Message}). Ask the user to check that the tool prints JSON only.");
        }

        var size = Utf8.GetByteCount(text);
        if (size <= MaxInlineJsonBytes)
        {
            return CapabilityResult.Success(data, $"Returned JSON ({FormatSize(size)}).");
        }

        File.WriteAllText(Path.Combine(context.ArtifactDirectory, "result.json"), text, Utf8);
        return CapabilityResult.Success(null, $"The JSON is too large to return inline, so it was written to result.json ({FormatSize(size)}).");
    }

    private CapabilityResult CompleteText(ProcessRunResult run)
    {
        var output = Mask(run.StdoutTail.TrimStart('﻿'));
        var data = new JsonObject
        {
            ["exitCode"] = run.ExitCode,
            ["output"] = output,
            ["truncated"] = run.StdoutTruncated,
        };
        var summary = run.StdoutTruncated
            ? $"'{Descriptor.Name}' finished; only the last {MaxTextChars / 1024} KB of its output are returned."
            : $"'{Descriptor.Name}' finished.";
        return CapabilityResult.Success(data, summary);
    }

    /// <summary>Substitutes the placeholders. Fails if a value contains a character no argument can carry.</summary>
    private bool TryBuildArguments(CapabilityContext context, string? outputFile, out List<string> arguments, out string error)
    {
        arguments = [];
        error = "";
        var parameters = Descriptor.Parameters;

        bool IsGiven(string param) => context.Args.ValueKind == JsonValueKind.Object
            && context.Args.TryGetProperty(param, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False);

        string ValueOf(string placeholder)
        {
            if (placeholder == CliCapabilityFactory.TargetPathPlaceholder)
            {
                return context.TargetPath ?? throw new InvalidOperationException("The capability needs a target path but the context has none.");
            }
            if (placeholder == CliCapabilityFactory.OutputFilePlaceholder)
            {
                return outputFile ?? throw new InvalidOperationException("The capability has no output file.");
            }
            return ToText(context.Args.GetProperty(placeholder));
        }

        bool CanFill(ArgTemplate template) =>
            template.Placeholders.All(p => !parameters.TryGetValue(p, out var parameter) || parameter.Required || IsGiven(p));

        var items = _command.Args
            .Concat(_command.OptionalArgs.Where(o => IsGiven(o.Param)).SelectMany(o => o.Items))
            .Where(CanFill);
        foreach (var item in items)
        {
            var argument = item.Render(ValueOf);
            if (argument.Contains('\0'))
            {
                error = "An arg contains a NUL character, which cannot be passed to a program. Send plain text and a new requestId.";
                return false;
            }
            arguments.Add(argument);
        }
        return true;
    }

    private static string ToText(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString()!;
            case JsonValueKind.Number when value.TryGetInt64(out var whole):
                return whole.ToString(CultureInfo.InvariantCulture);
            case JsonValueKind.Number when value.TryGetDecimal(out var number):
                return number.ToString(CultureInfo.InvariantCulture);
            case JsonValueKind.Number:
                return value.GetDouble().ToString("R", CultureInfo.InvariantCulture);
            default:
                throw new InvalidOperationException($"A {value.ValueKind} value cannot be used as a command-line argument.");
        }
    }

    private string Mask(string text)
    {
        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, Masked, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>Masks the secrets in every file the job may publish, including files the tool wrote itself.</summary>
    private void MaskArtifacts(string directory)
    {
        if (_secrets.Length == 0)
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            if (!_secrets.Any(secret => bytes.AsSpan().IndexOf(Utf8.GetBytes(secret)) >= 0))
            {
                continue;
            }
            File.WriteAllBytes(file, Utf8.GetBytes(Mask(Encoding.UTF8.GetString(bytes))));
        }
    }

    private static string LastChars(string text, int max, string whenEmpty)
    {
        if (text.Length == 0)
        {
            return whenEmpty;
        }
        return text.Length <= max ? text : "..." + text[^max..];
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture)} MB",
    };
}
