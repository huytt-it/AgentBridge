namespace AgentBridge;

/// <summary>Absolute locations of every folder and file the bridge uses, resolved once from options.</summary>
public sealed class AgentBridgePaths
{
    /// <summary>Folder inside Box Drive used when BoxRoot is not set. The cloud's standing instruction uses the same name.</summary>
    public const string DefaultBoxFolderName = "AgentBridge";

    /// <param name="boxDriveRoot">Box Drive's folder; <see cref="DefaultBoxDriveRoot"/> when null. Tests pass their own.</param>
    public AgentBridgePaths(AgentBridgeOptions options, string baseDirectory, string? boxDriveRoot = null)
    {
        BoxDriveRoot = boxDriveRoot ?? DefaultBoxDriveRoot;
        BoxRootIsDefault = string.IsNullOrWhiteSpace(options.BoxRoot);
        BoxRoot = BoxRootIsDefault
            ? Path.Combine(BoxDriveRoot, DefaultBoxFolderName)
            : Path.GetFullPath(options.BoxRoot, baseDirectory);
        LocalDataRoot = string.IsNullOrWhiteSpace(options.LocalDataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge")
            : Path.GetFullPath(options.LocalDataRoot, baseDirectory);
    }

    /// <summary>Where Box Drive keeps the synced files by default: %USERPROFILE%\Box.</summary>
    public static string DefaultBoxDriveRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Box");

    /// <summary>Relative paths in config resolve against the current directory, like any CLI tool.</summary>
    public static AgentBridgePaths ForCurrentDirectory(AgentBridgeOptions options, string? boxDriveRoot = null) =>
        new(options, Environment.CurrentDirectory, boxDriveRoot);

    /// <summary>Box Drive's folder. Without a configured BoxRoot, the bridge uses its AgentBridge subfolder.</summary>
    public string BoxDriveRoot { get; }

    /// <summary>True when BoxRoot is not configured and is the AgentBridge folder inside Box Drive.</summary>
    public bool BoxRootIsDefault { get; }

    // Box root: synced with the cloud.
    public string BoxRoot { get; }
    public string Inbox => Path.Combine(BoxRoot, "inbox");
    public string Responses => Path.Combine(BoxRoot, "responses");
    public string Artifacts => Path.Combine(BoxRoot, "artifacts");
    public string Archive => Path.Combine(BoxRoot, "archive");
    public string DeadLetter => Path.Combine(BoxRoot, "dead-letter");
    public string CatalogFile => Path.Combine(BoxRoot, "catalog.json");
    public string ReadmeFile => Path.Combine(BoxRoot, "README.md");

    // Local data root: never synced.
    public string LocalDataRoot { get; }
    public string Jobs => Path.Combine(LocalDataRoot, "jobs");

    /// <summary>The worker's daily log files, agentbridge-yyyyMMdd.log.</summary>
    public string Logs => Path.Combine(LocalDataRoot, "logs");

    /// <summary>Where files are written before being moved into Box, so no temp file is ever synced.</summary>
    public string Staging => Path.Combine(LocalDataRoot, "staging");

    public string LockFile => Path.Combine(LocalDataRoot, "agentbridge.lock");

    public static bool IsSameOrInside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
        {
            return true;
        }
        if (Path.IsPathRooted(relative))
        {
            return false; // different drive
        }
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
