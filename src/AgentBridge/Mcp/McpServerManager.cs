using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentBridge.Mcp;

/// <summary>
/// Starts configured MCP servers over STDIO on first use, keeps one connection per server and serializes calls to it.
/// A server that died is started again on the next use; a tool call is never retried, because it may have had effects.
/// </summary>
internal sealed class McpServerManager : IAsyncDisposable
{
    /// <summary>Logger category of the servers' stderr lines; raise its level in Logging:LogLevel to quiet chatty servers.</summary>
    public const string StderrCategory = "AgentBridge.Mcp.ServerStderr";

    private readonly AgentBridgeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpServerManager> _logger;
    private readonly ILogger _stderr;
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    public McpServerManager(IOptions<AgentBridgeOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpServerManager>();
        _stderr = loggerFactory.CreateLogger(StderrCategory);
    }

    /// <summary>Lists the server's tools, starting it if needed.</summary>
    /// <exception cref="McpServerException">The server is not configured, is disabled, could not start or stopped.</exception>
    public Task<IReadOnlyList<Tool>> ListToolsAsync(string server, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<Tool>>(server, async (client, token) =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: token);
            return tools.Select(t => t.ProtocolTool).ToList();
        }, cancellationToken);

    /// <summary>Calls one tool once. Errors the tool reports itself come back in the result (IsError).</summary>
    /// <exception cref="McpServerException">The server could not start, or stopped or closed the connection during the call.</exception>
    /// <exception cref="McpProtocolException">The server rejected the call, e.g. an unknown tool or invalid arguments.</exception>
    public Task<CallToolResult> CallToolAsync(
        string server, string tool, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken) =>
        UseAsync(server, async (client, token) =>
        {
            var request = new CallToolRequestParams { Name = tool, Arguments = new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal) };
            return await client.CallToolAsync(request, token);
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var (name, connection) in _connections)
        {
            // A call in progress was cancelled by the host before disposal; do not wait long for it.
            var entered = await connection.Gate.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                await CloseAsync(name, connection);
            }
            finally
            {
                if (entered)
                {
                    connection.Gate.Release();
                }
            }
        }
    }

    private async Task<T> UseAsync<T>(string server, Func<McpClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var configured = GetServerOptions(server);
        var connection = _connections.GetOrAdd(server, _ => new Connection());

        await connection.Gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (connection.Client is { Completion.IsCompleted: true } stopped)
            {
                _logger.LogWarning("MCP server '{Server}' has stopped ({Reason}); starting it again.", server, DescribeCompletion(stopped));
                await CloseAsync(server, connection);
            }
            connection.Client ??= await StartAsync(server, configured, cancellationToken);
            var client = connection.Client;

            try
            {
                return await action(client, cancellationToken);
            }
            catch (McpProtocolException)
            {
                throw; // the server answered: the connection is fine
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // the caller gave up; the connection stays, though the server may still finish the call
            }
            catch (Exception ex)
            {
                // The process died or the transport broke: the connection is unusable. Start fresh next time.
                // For a dead process the SDK's message holds the exit code and the end of its stderr.
                _logger.LogWarning(ex, "MCP server '{Server}' stopped or closed the connection during a request.", server);
                await CloseAsync(server, connection);
                throw new McpServerException(server, $"MCP server '{server}' stopped or closed the connection during the request: {ex.Message}", ex);
            }
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    private McpServerOptions GetServerOptions(string server)
    {
        if (!_options.McpServers.TryGetValue(server, out var configured))
        {
            var known = _options.McpServers.Count == 0
                ? "No servers are configured."
                : $"Configured servers: {string.Join(", ", _options.McpServers.Keys.Order(StringComparer.OrdinalIgnoreCase))}.";
            throw new McpServerException(server, $"MCP server '{server}' is not configured under AgentBridge:McpServers. {known}");
        }
        if (!configured.Enabled)
        {
            throw new McpServerException(server, $"MCP server '{server}' is disabled (AgentBridge:McpServers:{server}:Enabled is false).");
        }
        return configured;
    }

    private async Task<McpClient> StartAsync(string server, McpServerOptions configured, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(configured.StartupTimeoutSeconds);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server,
            Command = configured.Command,
            Arguments = configured.Args.ToList(),
            WorkingDirectory = string.IsNullOrWhiteSpace(configured.WorkingDirectory) ? null : configured.WorkingDirectory,
            EnvironmentVariables = configured.Environment.ToDictionary(e => e.Key, e => (string?)e.Value, StringComparer.OrdinalIgnoreCase),
            // Server stderr may hold paths and secrets: local log only.
            StandardErrorLines = line => _stderr.LogInformation("[{Server}] {Line}", server, line),
        }, _loggerFactory);

        _logger.LogInformation("Starting MCP server '{Server}': {Command}", server, configured.Command);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(timeout);
        try
        {
            var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "AgentBridge", Version = AgentVersion.Current },
                InitializationTimeout = timeout,
            }, _loggerFactory, startup.Token);
            _logger.LogInformation("MCP server '{Server}' is ready ({ServerName} {ServerVersion}).",
                server, client.ServerInfo?.Name ?? "unnamed", client.ServerInfo?.Version ?? "");
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpServerException(server,
                $"MCP server '{server}' did not complete the MCP handshake within {configured.StartupTimeoutSeconds} seconds. "
                + $"Check that '{configured.Command}' starts an MCP server over STDIO, or raise AgentBridge:McpServers:{server}:StartupTimeoutSeconds "
                + "if it is slow to start (npx downloads packages on first use).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpServerException(server,
                $"MCP server '{server}' could not be started with '{configured.Command}': {ex.Message} "
                + "Check Command, Args and WorkingDirectory under AgentBridge:McpServers, and run the command by hand to see its errors.", ex);
        }
    }

    private async Task CloseAsync(string server, Connection connection)
    {
        if (connection.Client is not { } client)
        {
            return;
        }
        connection.Client = null;
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing MCP server '{Server}' failed.", server);
        }
    }

    private static string DescribeCompletion(McpClient client)
    {
        var details = client.Completion.IsCompleted ? client.Completion.Result : null;
        return details switch
        {
            StdioClientCompletionDetails { ExitCode: { } code } => $"process exited with code {code.ToString(CultureInfo.InvariantCulture)}",
            { Exception: { } ex } => ex.Message,
            _ => "connection closed",
        };
    }

    private sealed class Connection
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public McpClient? Client { get; set; }
    }
}

/// <summary>An MCP server could not be used. The message may hold local details (command, stderr): keep it in the local log.</summary>
internal sealed class McpServerException : Exception
{
    public McpServerException(string server, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Server = server;
    }

    public string Server { get; }
}
