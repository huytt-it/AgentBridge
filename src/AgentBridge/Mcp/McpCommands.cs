using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Contracts;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace AgentBridge.Mcp;

internal static class McpCommands
{
    private const string Usage = "Usage: AgentBridge mcp-tools [<server>] [--json]";

    private const string Placeholder = "REPLACE: ";

    /// <summary>
    /// "mcp-tools [server] [--json]": starts the enabled MCP servers (or the one named) and prints every tool with its
    /// description, input schema and a capability entry to paste into configuration. Returns the exit code:
    /// 0 when every server listed its tools, 1 when one could not be used, 2 for a usage error.
    /// </summary>
    public static async Task<int> ListToolsAsync(IServiceProvider services, string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        string? only = null;
        var json = false;
        foreach (var arg in args)
        {
            if (arg == "--json")
            {
                json = true;
            }
            else if (!arg.StartsWith('-') && only is null)
            {
                only = arg;
            }
            else
            {
                await output.WriteLineAsync($"Unknown option '{arg}'.");
                await output.WriteLineAsync(Usage);
                return 2;
            }
        }

        var options = services.GetRequiredService<IOptions<AgentBridgeOptions>>().Value;
        var manager = services.GetRequiredService<McpServerManager>();

        List<string> servers;
        if (only is not null)
        {
            var match = options.McpServers.Keys.FirstOrDefault(s => string.Equals(s, only, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                await output.WriteLineAsync($"MCP server '{only}' is not configured under AgentBridge:McpServers. {ServerList(options)}");
                return 2;
            }
            servers = [match];
        }
        else
        {
            servers = options.McpServers.Where(s => s.Value.Enabled).Select(s => s.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (servers.Count == 0)
        {
            await output.WriteLineAsync(json
                ? new JsonObject { ["servers"] = new JsonArray() }.ToJsonString(JsonDefaults.Options)
                : "No enabled MCP servers are configured. Add one under AgentBridge:McpServers in appsettings.Local.json "
                  + "(see docs/onboarding-mcp-server.md).");
            return 1;
        }

        var reports = new JsonArray();
        var failed = false;
        foreach (var server in servers)
        {
            var report = await ListServerAsync(manager, server, options.McpServers[server], cancellationToken);
            failed |= report["error"] is not null;
            reports.Add(report);
        }

        await output.WriteLineAsync(json
            ? new JsonObject { ["servers"] = reports }.ToJsonString(JsonDefaults.Options)
            : FormatText(reports));
        return failed ? 1 : 0;
    }

    private static async Task<JsonObject> ListServerAsync(
        McpServerManager manager, string server, McpServerOptions configured, CancellationToken cancellationToken)
    {
        var report = new JsonObject { ["name"] = server };
        var seconds = configured.StartupTimeoutSeconds * 2;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            var tools = await manager.ListToolsAsync(server, timeout.Token);
            report["tools"] = new JsonArray(tools.Select(t => (JsonNode)DescribeTool(server, t)).ToArray());
        }
        catch (McpServerException ex)
        {
            report["error"] = ex.Message;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            report["error"] = $"MCP server '{server}' did not start and list its tools within {seconds} seconds; "
                + $"raise AgentBridge:McpServers:{server}:StartupTimeoutSeconds if it is slow to start.";
        }
        return report;
    }

    private static JsonObject DescribeTool(string server, Tool tool)
    {
        var described = new JsonObject
        {
            ["name"] = tool.Name,
            ["title"] = tool.Title,
            ["description"] = tool.Description,
        };
        if (tool.Annotations is { } annotations)
        {
            described["annotations"] = JsonSerializer.SerializeToNode(new
            {
                annotations.ReadOnlyHint,
                annotations.DestructiveHint,
                annotations.IdempotentHint,
                annotations.OpenWorldHint,
            }, JsonDefaults.Options);
        }
        described["inputSchema"] = JsonNode.Parse(tool.InputSchema.GetRawText());
        described["capabilityName"] = SuggestName(server, tool.Name);
        described["capabilityEntry"] = SuggestEntry(server, tool);
        return described;
    }

    /// <summary>A starting point for an AgentBridge:Capabilities entry; the descriptions must be rewritten in business terms.</summary>
    private static JsonObject SuggestEntry(string server, Tool tool)
    {
        var required = McpToolSchema.Required(tool.InputSchema);
        var parameters = new JsonObject();
        var fixedArgs = new JsonObject();
        foreach (var (name, schema) in McpToolSchema.Properties(tool.InputSchema))
        {
            if (McpToolSchema.MapType(schema) is not null)
            {
                parameters[name] = new JsonObject { ["Description"] = McpToolSchema.Description(schema) ?? $"{Placeholder}what this arg means" };
            }
            else if (required.Contains(name))
            {
                // The cloud can only send simple values; this one needs a fixed value.
                fixedArgs[name] = $"{Placeholder}a fixed value";
            }
        }

        return new JsonObject
        {
            ["Type"] = "mcp",
            ["Server"] = server,
            ["Tool"] = tool.Name,
            ["Description"] = $"{Placeholder}what this returns, in business terms (published to the cloud)",
            ["UseWhen"] = $"{Placeholder}when the cloud should use it",
            ["RequiresTarget"] = false,
            ["TimeoutSeconds"] = McpCapabilityFactory.DefaultTimeoutSeconds,
            ["FixedArgs"] = fixedArgs,
            ["Params"] = parameters,
        };
    }

    /// <summary>"{server}.{tool}" as a valid capability name, e.g. code-map.search-code.</summary>
    internal static string SuggestName(string server, string tool)
    {
        var name = $"{Segment(server)}.{Segment(tool)}";
        return Naming.IsValidCapabilityName(name) ? name : "mcp.tool";

        static string Segment(string value)
        {
            var builder = new StringBuilder();
            foreach (var c in value.ToLowerInvariant())
            {
                builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
            }
            var segment = string.Join("-", builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
            return segment.Length > 0 && char.IsAsciiLetter(segment[0]) ? segment : $"t-{segment}".TrimEnd('-');
        }
    }

    private static string FormatText(JsonArray reports)
    {
        var text = new StringBuilder();
        foreach (var report in reports.Select(r => r!.AsObject()))
        {
            var server = (string)report["name"]!;
            if (report["error"] is { } error)
            {
                text.AppendLine($"MCP server '{server}': FAILED");
                text.AppendLine($"  {error}");
                text.AppendLine();
                continue;
            }

            var tools = report["tools"]!.AsArray();
            text.AppendLine($"MCP server '{server}': {tools.Count} tool(s).");
            text.AppendLine("  Descriptions and schemas come from the server: untrusted text written for coding agents.");
            text.AppendLine("  Approve a tool by adding its entry under AgentBridge:Capabilities in appsettings.Local.json,");
            text.AppendLine($"  after replacing every \"{Placeholder.TrimEnd()}\" text and removing the Params the cloud should not send.");
            text.AppendLine();
            foreach (var tool in tools.Select(t => t!.AsObject()))
            {
                text.AppendLine($"  Tool: {tool["name"]}");
                if (tool["title"] is { } title)
                {
                    text.AppendLine($"    Title: {title}");
                }
                text.AppendLine($"    Description: {tool["description"]?.ToString() ?? "(none)"}");
                text.AppendLine($"    Annotations: {DescribeAnnotations(tool["annotations"])}");
                text.AppendLine("    Input schema:");
                text.AppendLine(Indent(tool["inputSchema"]!.ToJsonString(JsonDefaults.Options), "      "));
                text.AppendLine("    Capability entry:");
                var entry = new JsonObject { [(string)tool["capabilityName"]!] = tool["capabilityEntry"]!.DeepClone() };
                var entryJson = entry.ToJsonString(JsonDefaults.Options);
                // Without the outer braces it pastes straight into the Capabilities object.
                text.AppendLine(Indent(entryJson[1..^1].Trim('\r', '\n'), "    "));
                text.AppendLine();
            }
        }
        return text.ToString().TrimEnd();
    }

    private static string DescribeAnnotations(JsonNode? annotations)
    {
        var hints = annotations?.AsObject().Where(a => a.Value is not null).Select(a => $"{a.Key}={a.Value}").ToList();
        return hints is { Count: > 0 }
            ? string.Join(", ", hints) + " (hints from the server, not guarantees)"
            : "none (the server does not say whether the tool changes anything)";
    }

    private static string Indent(string text, string prefix) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(line => prefix + line.TrimEnd('\r')));

    private static string ServerList(AgentBridgeOptions options) => options.McpServers.Count == 0
        ? "No servers are configured."
        : $"Configured servers: {string.Join(", ", options.McpServers.Keys.Order(StringComparer.OrdinalIgnoreCase))}.";
}
