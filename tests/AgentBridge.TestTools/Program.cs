using AgentBridge.TestTools;

// Stand-in tools so CLI and MCP capabilities can be tested without the real tools:
//   cli <scenario> ...     a fake command-line tool (CliFixture)
//   mcp-server ...         a fake MCP server over STDIO (McpFixture)
return args switch
{
    ["cli", .. var rest] => await CliFixture.RunAsync(rest),
    ["mcp-server", .. var rest] => await McpFixture.RunAsync(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: AgentBridge.TestTools cli <scenario> [...] | mcp-server [...]");
    return 2;
}
