using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentBridge.TestTools;

/// <summary>
/// A stand-in MCP server over STDIO. stdout carries the protocol only; everything else goes to stderr.
/// Options: --hang (reads stdin but never answers the handshake), --fail-start (exits 3 before the handshake).
/// Every instance writes "fixture pid=N" to stderr first, and an empty {pid}.pid file into the folder named by
/// AGENTBRIDGE_FIXTURE_PIDS when set, so tests can check that no process is left running.
/// </summary>
internal static class McpFixture
{
    public const string MarkerVariable = "AGENTBRIDGE_FIXTURE_MARKER";
    public const string PidFolderVariable = "AGENTBRIDGE_FIXTURE_PIDS";

    public static async Task<int> RunAsync(string[] args)
    {
        if (Environment.GetEnvironmentVariable(PidFolderVariable) is { Length: > 0 } pidFolder)
        {
            File.WriteAllText(Path.Combine(pidFolder, $"{Environment.ProcessId}.pid"), "");
        }
        Console.Error.WriteLine($"fixture pid={Environment.ProcessId}");
        if (args.Contains("--fail-start"))
        {
            Console.Error.WriteLine("fixture: failing on purpose before the handshake");
            return 3;
        }
        if (args.Contains("--hang"))
        {
            await Console.In.ReadToEndAsync();
            return 0;
        }

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "agentbridge-fixture", Version = "1.0.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = CreateTools() }),
                CallToolHandler = CallToolAsync,
            },
        };
        await using var server = McpServer.Create(new StdioServerTransport(options, loggerFactory: null), options);
        await server.RunAsync();
        return 0;
    }

    private static List<Tool> CreateTools() =>
    [
        Tool("echo", "Echoes the message back. Use it to test the connection.", """
            {"type":"object","properties":{"message":{"type":"string","description":"Text to send back."}},"required":["message"]}
            """, new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false }),
        Tool("search_code", "Searches source files under root for the query.", """
            {"type":"object","properties":{
              "query":{"type":"string","description":"Text to search for."},
              "max_results":{"type":"integer","description":"Largest number of matches to return."},
              "root":{"type":"string","description":"Absolute folder to search in."}},
             "required":["query","root"]}
            """),
        Tool("structured", "Returns structured content.", """{"type":"object","properties":{}}"""),
        Tool("info", "Returns the process id, working directory and marker environment variable.", """{"type":"object"}"""),
        Tool("fail", "Always reports a tool error.", """
            {"type":"object","properties":{"length":{"type":"integer","description":"Length of the error text."}}}
            """),
        Tool("slow", "Waits before answering.", """
            {"type":"object","properties":{"seconds":{"type":"number","description":"How long to wait."}},"required":["seconds"]}
            """),
        Tool("crash", "Ends the server process.", """{"type":"object","properties":{}}"""),
        Tool("big", "Returns a large text.", """
            {"type":"object","properties":{"kb":{"type":"integer","description":"Size of the text in kilobytes."}},"required":["kb"]}
            """),
        Tool("mixed", null, """{"type":"object","properties":{}}"""),
    ];

    private static Tool Tool(string name, string? description, string inputSchema, ToolAnnotations? annotations = null) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonSerializer.Deserialize<JsonElement>(inputSchema),
        Annotations = annotations,
    };

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var name = request.Params?.Name ?? "";
        var args = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        Console.Error.WriteLine($"fixture: call {name}");

        switch (name)
        {
            case "echo":
                return Text(String(args, "message"));
            case "search_code":
                var maxResults = args.TryGetValue("max_results", out var max) ? max.GetInt64().ToString() : "-";
                return Text($"root={String(args, "root")}; query={String(args, "query")}; max_results={maxResults}");
            case "structured":
                return Structured(new JsonObject
                {
                    ["items"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "alpha" }, new JsonObject { ["id"] = 2, ["name"] = "beta" }),
                    ["total"] = 2,
                });
            case "info":
                return Structured(new JsonObject
                {
                    ["pid"] = Environment.ProcessId,
                    ["cwd"] = Environment.CurrentDirectory,
                    ["marker"] = Environment.GetEnvironmentVariable(MarkerVariable),
                });
            case "fail":
                var length = args.TryGetValue("length", out var l) ? l.GetInt32() : 0;
                var message = "Something went wrong in the tool.";
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = length > message.Length ? message + new string('x', length - message.Length) : message }],
                };
            case "slow":
                await Task.Delay(TimeSpan.FromSeconds(args["seconds"].GetDouble()), cancellationToken);
                return Text("done");
            case "crash":
                Console.Error.WriteLine("fixture: crashing on purpose");
                Environment.Exit(3);
                return Text("unreachable");
            case "big":
                var text = new StringBuilder();
                var line = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ+\n"; // 64 bytes
                for (var i = 0; i < args["kb"].GetInt32() * 16; i++)
                {
                    text.Append(line);
                }
                return Text(text.ToString());
            case "mixed":
                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock { Text = "first" },
                        ImageContentBlock.FromBytes(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png"),
                        new ResourceLinkBlock { Uri = "file:///readme.md", Name = "readme" },
                        new TextContentBlock { Text = "second" },
                    ],
                };
            default:
                throw new McpProtocolException($"Unknown tool '{name}'.", McpErrorCode.InvalidParams);
        }
    }

    private static string String(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) ? value.GetString() ?? "" : "";

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Structured(JsonObject value) => new()
    {
        Content = [new TextContentBlock { Text = value.ToJsonString() }],
        StructuredContent = JsonSerializer.SerializeToElement(value),
    };
}
