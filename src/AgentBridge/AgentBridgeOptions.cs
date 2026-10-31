namespace AgentBridge;

/// <summary>Bound from the "AgentBridge" configuration section.</summary>
public sealed class AgentBridgeOptions
{
    public const string SectionName = "AgentBridge";

    /// <summary>Identifies this machine in responses and in catalog.json.</summary>
    public string AgentId { get; set; } = Environment.MachineName.ToLowerInvariant();

    /// <summary>
    /// The Box-synced folder that holds inbox/, responses/ and the rest.
    /// Relative paths resolve against the current directory.
    /// </summary>
    public string BoxRoot { get; set; } = "";

    /// <summary>Local, never-synced folder for job state and logs. Defaults to %LOCALAPPDATA%/AgentBridge.</summary>
    public string? LocalDataRoot { get; set; }

    public int PollSeconds { get; set; } = 5;

    /// <summary>How long an unreadable inbox file is treated as still syncing before it goes to dead-letter.</summary>
    public int IncompleteFileGraceSeconds { get; set; } = 60;

    /// <summary>Largest total size of the artifacts one job may publish to Box.</summary>
    public int MaxArtifactMegabytes { get; set; } = 20;

    /// <summary>Allowlisted targets the cloud may name. Keys are target names.</summary>
    public Dictionary<string, TargetOptions> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Local MCP servers the bridge may start. Keys are the server names "mcp" capabilities refer to.</summary>
    public Dictionary<string, McpServerOptions> McpServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-capability settings. Keys are capability names.</summary>
    public Dictionary<string, CapabilityOptions> Capabilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TargetOptions
{
    /// <summary>Absolute local path. Never published.</summary>
    public string Path { get; set; } = "";

    /// <summary>Shown to the cloud in catalog.json. Never put local paths here.</summary>
    public string Description { get; set; } = "";
}

/// <summary>
/// One capability entry. "native" entries only tune a built-in capability; "cli" and "mcp" entries define
/// a whole capability from configuration. Fields that do not apply to the entry's Type are ignored.
/// </summary>
public sealed class CapabilityOptions
{
    /// <summary>"native" (default), "cli" or "mcp".</summary>
    public string Type { get; set; } = "native";

    public bool Enabled { get; set; } = true;

    /// <summary>What the capability returns, in business terms. Required for cli; overrides the built-in or MCP text otherwise.</summary>
    public string? Description { get; set; }

    /// <summary>When the cloud should choose this capability.</summary>
    public string? UseWhen { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>cli and mcp: the cloud must name one of the configured targets.</summary>
    public bool RequiresTarget { get; set; }

    /// <summary>cli and mcp: arguments the cloud may send, keyed by camelCase name.</summary>
    public Dictionary<string, ParameterOptions> Params { get; set; } = new();

    /// <summary>cli and mcp: example args for catalog.json.</summary>
    public Dictionary<string, string> ExampleArgs { get; set; } = new();

    // ---- cli ----

    /// <summary>cli: executable name on PATH, or an absolute path.</summary>
    public string? Exe { get; set; }

    /// <summary>
    /// cli: argument template. Each item is one argument. "{name}" is replaced by the value of param "name",
    /// "{targetPath}" by the target's local path, "{outputFile}" by a file the tool writes its main result to.
    /// </summary>
    public List<string> Args { get; set; } = [];

    /// <summary>cli: arguments added only when the named param is given, e.g. "since": ["--since", "{since}"].</summary>
    public Dictionary<string, List<string>> OptionalArgs { get; set; } = new();

    /// <summary>cli: working directory when the capability has no target. Defaults to the job's own folder.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>cli: format of the main result: markdown, csv, json or text. See OutputFormats.</summary>
    public string? Output { get; set; }

    /// <summary>cli: environment variables set for the tool. Values are masked in any output that leaves the job folder.</summary>
    public Dictionary<string, string> Environment { get; set; } = new();

    // ---- mcp ----

    /// <summary>mcp: key of the server in AgentBridge:McpServers.</summary>
    public string? Server { get; set; }

    /// <summary>mcp: tool name as listed by the server.</summary>
    public string? Tool { get; set; }

    /// <summary>mcp: arguments always sent to the tool and never exposed to the cloud, e.g. a root folder.</summary>
    public Dictionary<string, string> FixedArgs { get; set; } = new();
}

/// <summary>An argument the cloud may send to a cli or mcp capability. Mirrors Contracts.ParameterDescriptor.</summary>
public sealed class ParameterOptions
{
    /// <summary>string, integer, number, boolean, object or array.</summary>
    public string Type { get; set; } = "string";

    public bool Required { get; set; }

    public string Description { get; set; } = "";

    /// <summary>Regex a string value must match in full.</summary>
    public string? Pattern { get; set; }

    public string? Example { get; set; }
}

/// <summary>A local MCP server started over STDIO.</summary>
public sealed class McpServerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Executable that starts the server, e.g. "node", "npx" or an absolute path.</summary>
    public string Command { get; set; } = "";

    public List<string> Args { get; set; } = [];

    public string? WorkingDirectory { get; set; }

    /// <summary>Environment variables for the server. Put secrets in appsettings.Local.json or user environment variables.</summary>
    public Dictionary<string, string> Environment { get; set; } = new();

    public int StartupTimeoutSeconds { get; set; } = 30;
}
