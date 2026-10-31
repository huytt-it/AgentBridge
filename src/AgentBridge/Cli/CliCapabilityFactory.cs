using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;

namespace AgentBridge.Cli;

/// <summary>
/// Turns AgentBridge:Capabilities entries with Type "cli" into capabilities that run a local executable.
/// Everything is checked here, at startup, so a request can only ever fill in validated values.
/// </summary>
internal sealed class CliCapabilityFactory : ICapabilityFactory
{
    public const string TargetPathPlaceholder = "targetPath";
    public const string OutputFilePlaceholder = "outputFile";
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>For string params without a Pattern: no leading '-' (a tool would read it as an option) and no line breaks.</summary>
    public const string DefaultStringPattern = @"^(?!-)[^\r\n]{0,500}\z";

    private static readonly string[] OutputNames =
        [OutputFormats.Markdown, OutputFormats.Csv, OutputFormats.Json, OutputFormats.Text];

    private static readonly string[] ParameterTypeNames =
        [ParameterTypes.String, ParameterTypes.Integer, ParameterTypes.Number, ParameterTypes.Boolean];

    private static readonly string[] BatchExtensions = [".bat", ".cmd"];
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    private readonly ProcessRunner _runner;
    private readonly ILoggerFactory _loggerFactory;

    public CliCapabilityFactory(ProcessRunner runner, ILoggerFactory loggerFactory)
    {
        _runner = runner;
        _loggerFactory = loggerFactory;
    }

    public string Type => "cli";

    /// <summary>Folders searched for a bare Exe name, separated like PATH. The PATH variable when null (tests set it).</summary>
    internal string? SearchPath { get; init; }

    public ICapability? Create(string name, CapabilityOptions options, IList<string> errors)
    {
        var report = new Report($"{AgentBridgeOptions.SectionName}:Capabilities:{name}", errors);

        var exePath = ResolveExecutable(options.Exe, report);
        if (string.IsNullOrWhiteSpace(options.Description))
        {
            report.Add("Description", "is required: say what the tool returns, in business terms. The cloud reads it in catalog.json.");
        }
        var output = ParseOutput(options.Output, report);
        var parameters = BuildParameters(options, report);
        var args = ParseTemplates(options.Args, "Args", report);
        var optionalArgs = new List<(string Param, IReadOnlyList<ArgTemplate> Items)>();
        foreach (var (param, items) in options.OptionalArgs.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            optionalArgs.Add((param, ParseTemplates(items, $"OptionalArgs:{param}", report)));
        }
        CheckPlaceholders(options, output, parameters, args, optionalArgs, report);

        string? workingDirectory = null;
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            workingDirectory = Path.GetFullPath(options.WorkingDirectory);
            if (!Directory.Exists(workingDirectory))
            {
                report.Add("WorkingDirectory", $"'{workingDirectory}' does not exist. Create it or point to an existing folder.");
            }
        }
        foreach (var variable in options.Environment.Keys)
        {
            if (string.IsNullOrWhiteSpace(variable) || variable.Contains('='))
            {
                report.Add($"Environment:{variable}", "is not a valid environment variable name.");
            }
        }
        var example = BuildExampleArgs(options, parameters, report);

        if (report.HasErrors)
        {
            return null;
        }

        var descriptor = new CapabilityDescriptor
        {
            Name = name,
            Description = options.Description!,
            UseWhen = string.IsNullOrWhiteSpace(options.UseWhen) ? null : options.UseWhen,
            RequiresTarget = options.RequiresTarget,
            Parameters = parameters,
            Output = output!,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds ?? DefaultTimeoutSeconds),
            ExampleArgs = example,
        };
        var command = new CliCommand(
            exePath!,
            output!,
            options.RequiresTarget,
            workingDirectory,
            args,
            optionalArgs,
            new Dictionary<string, string>(options.Environment, StringComparer.Ordinal));
        return new CliCapability(descriptor, command, _runner, _loggerFactory.CreateLogger<CliCapability>());
    }

    // ---- Exe ----

    private string? ResolveExecutable(string? exe, Report report)
    {
        if (string.IsNullOrWhiteSpace(exe))
        {
            report.Add("Exe", "is required: the program to run, either a name found on PATH (e.g. \"node\") or an absolute path.");
            return null;
        }
        exe = exe.Trim();

        if (IsBatchFile(exe))
        {
            report.Add("Exe", BatchFileMessage(exe));
            return null;
        }

        string? resolved;
        if (Path.IsPathRooted(exe) || exe.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            if (!Path.IsPathFullyQualified(exe))
            {
                report.Add("Exe", $"'{exe}' is a relative path. Use an absolute path, or a bare name that is found on PATH.");
                return null;
            }
            if (!File.Exists(exe))
            {
                report.Add("Exe", $"'{exe}' does not exist. Check the path of the program.");
                return null;
            }
            resolved = exe;
        }
        else
        {
            resolved = FindOnPath(exe);
            if (resolved is null)
            {
                report.Add("Exe", $"'{exe}' was not found on PATH. Install it, add its folder to PATH, or use its absolute path.");
                return null;
            }
            if (IsBatchFile(resolved))
            {
                report.Add("Exe", BatchFileMessage(resolved));
                return null;
            }
        }
        return resolved;
    }

    private static bool IsBatchFile(string path) =>
        BatchExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string BatchFileMessage(string path) =>
        $"'{path}' is a Windows batch file. Windows runs .bat and .cmd files through cmd.exe, which re-reads the arguments "
        + "(& | ^ % and so on) and makes command injection possible, so they are refused. Call the real program instead, e.g. "
        + "Exe \"node\" with Args [\"script.js\"], \"python\" with [\"script.py\"] or "
        + "\"powershell\" with [\"-NoProfile\", \"-File\", \"script.ps1\"].";

    /// <summary>Searches PATH like the shell does (PATHEXT on Windows) and returns the full path of the first match.</summary>
    private string? FindOnPath(string name)
    {
        var folders = (SearchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            // A file without an extension, such as npm, is a shell script that Windows cannot start.
            if (Path.HasExtension(name))
            {
                candidates.Add(name);
            }
            var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            candidates.AddRange(pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ext => name + ext));
        }
        else
        {
            candidates.Add(name);
        }

        foreach (var folder in folders)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var path = Path.Combine(folder.Trim('"'), candidate);
                    if (File.Exists(path))
                    {
                        return Path.GetFullPath(path);
                    }
                }
                catch (ArgumentException)
                {
                    // A PATH entry with invalid characters: skip it, as the shell does.
                }
            }
        }
        return null;
    }

    // ---- Output ----

    private static string? ParseOutput(string? value, Report report)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OutputFormats.Text;
        }
        var output = value.Trim().ToLowerInvariant();
        if (!OutputNames.Contains(output))
        {
            report.Add("Output", $"'{value}' is not supported; use one of {string.Join(", ", OutputNames)}.");
            return null;
        }
        return output;
    }

    // ---- Params ----

    private static Dictionary<string, ParameterDescriptor> BuildParameters(CapabilityOptions options, Report report)
    {
        var parameters = new Dictionary<string, ParameterDescriptor>(StringComparer.Ordinal);
        foreach (var (name, parameter) in options.Params)
        {
            var field = $"Params:{name}";
            if (name is TargetPathPlaceholder or OutputFilePlaceholder)
            {
                report.Add(field, $"uses a reserved name; '{name}' is a placeholder the bridge fills in itself. Choose another name.");
            }

            var type = (string.IsNullOrWhiteSpace(parameter.Type) ? ParameterTypes.String : parameter.Type.Trim().ToLowerInvariant());
            if (type is ParameterTypes.Object or ParameterTypes.Array)
            {
                report.Add($"{field}:Type", $"'{type}' is not supported for cli tools, because a command-line argument is text. Use {string.Join(", ", ParameterTypeNames)}.");
                type = ParameterTypes.String;
            }
            else if (!ParameterTypeNames.Contains(type))
            {
                report.Add($"{field}:Type", $"'{parameter.Type}' is not a known type; use one of {string.Join(", ", ParameterTypeNames)}.");
                type = ParameterTypes.String;
            }

            if (type == ParameterTypes.Boolean && parameter.Required)
            {
                report.Add($"{field}:Required", "cannot be true for a boolean: a boolean is an optional switch that OptionalArgs adds when it is true.");
            }

            string? pattern = null;
            if (type == ParameterTypes.String)
            {
                pattern = string.IsNullOrWhiteSpace(parameter.Pattern) ? DefaultStringPattern : parameter.Pattern;
                try
                {
                    _ = new Regex(pattern);
                }
                catch (ArgumentException ex)
                {
                    report.Add($"{field}:Pattern", $"is not a valid regular expression: {ex.Message}");
                }
            }

            JsonNode? example = null;
            if (!string.IsNullOrEmpty(parameter.Example))
            {
                example = ConvertValue(parameter.Example, type, pattern, $"{field}:Example", report);
            }

            parameters[name] = new ParameterDescriptor
            {
                Type = type,
                Required = parameter.Required,
                Description = parameter.Description,
                Pattern = pattern,
                Example = example,
            };
        }
        return parameters;
    }

    private static JsonObject? BuildExampleArgs(
        CapabilityOptions options, Dictionary<string, ParameterDescriptor> parameters, Report report)
    {
        var example = new JsonObject();
        foreach (var (name, text) in options.ExampleArgs)
        {
            var field = $"ExampleArgs:{name}";
            if (!parameters.TryGetValue(name, out var parameter))
            {
                report.Add(field, $"is not a declared param. Declared: {DescribeNames(parameters.Keys)}.");
                continue;
            }
            var value = ConvertValue(text, parameter.Type, parameter.Pattern, field, report);
            if (value is not null)
            {
                example[name] = value;
            }
        }
        return example.Count == 0 ? null : example;
    }

    /// <summary>Converts the configured text into the param's JSON type; reports a value the param would reject.</summary>
    private static JsonNode? ConvertValue(string text, string type, string? pattern, string field, Report report)
    {
        switch (type)
        {
            case ParameterTypes.Integer when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole):
                return JsonValue.Create(whole);
            case ParameterTypes.Number when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                return JsonValue.Create(number);
            case ParameterTypes.Boolean when bool.TryParse(text, out var flag):
                return JsonValue.Create(flag);
            case ParameterTypes.String:
                if (pattern is not null && !MatchesPattern(pattern, text))
                {
                    report.Add(field, $"'{text}' does not match the param's Pattern, so a request with it would be rejected.");
                    return null;
                }
                return JsonValue.Create(text);
            default:
                report.Add(field, $"'{text}' is not a valid {type} value.");
                return null;
        }
    }

    // ---- Args ----

    private static List<ArgTemplate> ParseTemplates(IEnumerable<string> items, string field, Report report)
    {
        var templates = new List<ArgTemplate>();
        var index = 0;
        foreach (var item in items)
        {
            if (ArgTemplate.TryParse(item ?? "", out var template, out var error))
            {
                templates.Add(template);
            }
            else
            {
                report.Add($"{field}[{index}]", error + ".");
            }
            index++;
        }
        return templates;
    }

    private static void CheckPlaceholders(
        CapabilityOptions options,
        string? output,
        Dictionary<string, ParameterDescriptor> parameters,
        List<ArgTemplate> args,
        List<(string Param, IReadOnlyList<ArgTemplate> Items)> optionalArgs,
        Report report)
    {
        var usedInArgs = new HashSet<string>(StringComparer.Ordinal);
        var withOutputFile = output is OutputFormats.Markdown or OutputFormats.Csv;
        var openedByPlaceholder = new HashSet<string>(StringComparer.Ordinal);

        string? ProblemWith(string placeholder, string? optionalKey)
        {
            if (placeholder == TargetPathPlaceholder)
            {
                return options.RequiresTarget ? null : "{targetPath} needs \"RequiresTarget\": true, because the path comes from the request's target.";
            }
            if (placeholder == OutputFilePlaceholder)
            {
                return withOutputFile ? null : "{outputFile} is only available when Output is markdown or csv.";
            }
            if (!parameters.TryGetValue(placeholder, out var parameter))
            {
                return $"{{{placeholder}}} is not a declared param. Declared: {DescribeNames(parameters.Keys)}.";
            }
            if (parameter.Type == ParameterTypes.Boolean)
            {
                return $"{{{placeholder}}} is a boolean. A boolean only switches its OptionalArgs entry on or off and has no value to insert.";
            }
            if (optionalKey is not null && !parameter.Required && placeholder != optionalKey)
            {
                return $"{{{placeholder}}} is optional, so it has no value when it is not sent. Use it in its own OptionalArgs entry '{placeholder}'.";
            }
            return null;
        }

        void Check(ArgTemplate template, string field, string? optionalKey)
        {
            foreach (var placeholder in template.Placeholders.Distinct())
            {
                if (ProblemWith(placeholder, optionalKey) is { } problem)
                {
                    report.Add(field, $"'{template.Text}': {problem}");
                }
            }
            if (template.LeadingPlaceholder is { } leading
                && parameters.TryGetValue(leading, out var parameter)
                && parameter.Type == ParameterTypes.String
                && parameter.Pattern is { } pattern
                && pattern != DefaultStringPattern
                && openedByPlaceholder.Add(leading)
                && MatchesPattern(pattern, "-x"))
            {
                report.Add($"Params:{leading}:Pattern",
                    $"accepts values that start with '-', and '{template.Text}' puts the value where a tool would read it as an option. "
                    + "Begin the Pattern with (?!-), or put the value after an option, e.g. --name={name}.");
            }
        }

        for (var i = 0; i < args.Count; i++)
        {
            Check(args[i], $"Args[{i}]", optionalKey: null);
            usedInArgs.UnionWith(args[i].Placeholders);
        }

        var optionalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (param, items) in optionalArgs)
        {
            optionalKeys.Add(param);
            if (!parameters.TryGetValue(param, out var parameter))
            {
                report.Add($"OptionalArgs:{param}", $"is not a declared param. Declared: {DescribeNames(parameters.Keys)}.");
                continue;
            }
            for (var i = 0; i < items.Count; i++)
            {
                Check(items[i], $"OptionalArgs:{param}[{i}]", optionalKey: param);
            }
            if (parameter.Type != ParameterTypes.Boolean && !items.Any(item => item.Placeholders.Contains(param)))
            {
                report.Add($"OptionalArgs:{param}", $"never uses {{{param}}}, so the value the request sends would be ignored.");
            }
        }

        foreach (var (name, parameter) in parameters)
        {
            if (parameter.Type == ParameterTypes.Boolean)
            {
                if (!optionalKeys.Contains(name))
                {
                    report.Add($"Params:{name}", $"is a boolean but OptionalArgs has no entry '{name}'. Add e.g. \"{name}\": [\"--{name}\"].");
                }
            }
            else if (parameter.Required && !usedInArgs.Contains(name))
            {
                report.Add($"Params:{name}", $"is required but no Args item uses {{{name}}}. Add it to Args, or set Required to false and use OptionalArgs.");
            }
            else if (!parameter.Required && !usedInArgs.Contains(name) && !optionalKeys.Contains(name))
            {
                report.Add($"Params:{name}", $"is declared but used nowhere. Add {{{name}}} to Args, add an OptionalArgs entry '{name}', or remove the param.");
            }
        }
    }

    private static string DescribeNames(IEnumerable<string> names)
    {
        var list = names.Order(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list);
    }

    /// <summary>Same matching as ArgsValidator: the whole value must match.</summary>
    private static bool MatchesPattern(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, $"^(?:{pattern})\\z", RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Adds problems to the shared error list, each prefixed with the config key of the field.</summary>
    private sealed class Report
    {
        private readonly string _key;
        private readonly IList<string> _errors;
        private readonly int _before;

        public Report(string key, IList<string> errors)
        {
            _key = key;
            _errors = errors;
            _before = errors.Count;
        }

        public bool HasErrors => _errors.Count > _before;

        public void Add(string field, string message) => _errors.Add($"{_key}:{field} {message}");
    }
}
