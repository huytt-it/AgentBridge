using AgentBridge.Contracts;
using Microsoft.Extensions.Options;

namespace AgentBridge;

/// <summary>Fails fast on configuration that would make the worker unsafe or unable to run.</summary>
internal sealed class AgentBridgeOptionsValidator : IValidateOptions<AgentBridgeOptions>
{
    private readonly string? _boxDriveRoot;

    /// <param name="boxDriveRoot">Box Drive's folder; <see cref="AgentBridgePaths.DefaultBoxDriveRoot"/> when null. Tests pass their own.</param>
    public AgentBridgeOptionsValidator(string? boxDriveRoot = null)
    {
        _boxDriveRoot = boxDriveRoot;
    }

    public ValidateOptionsResult Validate(string? name, AgentBridgeOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.AgentId))
        {
            errors.Add("AgentBridge:AgentId is required.");
        }

        // Resolved exactly as the worker resolves it, so the check and the folder in use never differ.
        var paths = AgentBridgePaths.ForCurrentDirectory(options, _boxDriveRoot);
        if (paths.BoxRootIsDefault && !Directory.Exists(paths.BoxDriveRoot))
        {
            errors.Add($"AgentBridge:BoxRoot is not set and Box Drive was not found at {paths.BoxDriveRoot}; "
                + "install and sign in to Box Drive, or set BoxRoot in appsettings.Local.json.");
        }
        else if (AgentBridgePaths.IsSameOrInside(paths.LocalDataRoot, paths.BoxRoot))
        {
            errors.Add($"AgentBridge:LocalDataRoot ({paths.LocalDataRoot}) must be outside BoxRoot so job state and logs are never synced.");
        }
        if (options.PollSeconds is < 1 or > 3600)
        {
            errors.Add("AgentBridge:PollSeconds must be between 1 and 3600.");
        }
        if (options.IncompleteFileGraceSeconds is < 0 or > 86400)
        {
            errors.Add("AgentBridge:IncompleteFileGraceSeconds must be between 0 and 86400.");
        }
        if (options.MaxArtifactMegabytes is < 1 or > 1024)
        {
            errors.Add("AgentBridge:MaxArtifactMegabytes must be between 1 and 1024.");
        }

        foreach (var (serverName, server) in options.McpServers)
        {
            if (!Naming.IsValidTargetName(serverName))
            {
                errors.Add($"MCP server name '{serverName}' is invalid: start with a letter, then letters, digits, '.', '_' or '-'.");
            }
            if (string.IsNullOrWhiteSpace(server.Command))
            {
                errors.Add($"AgentBridge:McpServers:{serverName}:Command is required, e.g. \"node\" or an absolute path.");
            }
            if (server.StartupTimeoutSeconds is < 1 or > 600)
            {
                errors.Add($"AgentBridge:McpServers:{serverName}:StartupTimeoutSeconds must be between 1 and 600.");
            }
        }

        foreach (var (targetName, target) in options.Targets)
        {
            if (!Naming.IsValidTargetName(targetName))
            {
                errors.Add($"Target name '{targetName}' is invalid: start with a letter, then letters, digits, '.', '_' or '-'.");
            }
            if (string.IsNullOrWhiteSpace(target.Path) || !Path.IsPathFullyQualified(target.Path))
            {
                errors.Add($"AgentBridge:Targets:{targetName}:Path must be an absolute path.");
            }
            else if (!Directory.Exists(target.Path))
            {
                errors.Add($"AgentBridge:Targets:{targetName}:Path does not exist: {target.Path}");
            }
            if (string.IsNullOrWhiteSpace(target.Description))
            {
                errors.Add($"AgentBridge:Targets:{targetName}:Description is required; it is what the cloud sees.");
            }
        }

        foreach (var (capabilityName, capability) in options.Capabilities)
        {
            if (!Naming.IsValidCapabilityName(capabilityName))
            {
                errors.Add($"Capability name '{capabilityName}' is invalid: use lowercase dotted names such as 'backlog.export'.");
            }
            if (capability.TimeoutSeconds is < 1 or > 86400)
            {
                errors.Add($"AgentBridge:Capabilities:{capabilityName}:TimeoutSeconds must be between 1 and 86400.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
