namespace AgentBridge.Tests.TestSupport;

/// <summary>Locates the stand-in tools built from tests/AgentBridge.TestTools (copied next to the test assembly).</summary>
public static class TestTools
{
    public static string DllPath { get; } = Path.Combine(AppContext.BaseDirectory, "AgentBridge.TestTools.dll");

    /// <summary>The dotnet host to start the tools with: dotnet test sets DOTNET_HOST_PATH.</summary>
    public static string DotnetPath { get; } =
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path ? path : "dotnet";

    /// <summary>Arguments that start the stand-in tools with the given sub-command, e.g. Args("cli", "backlog").</summary>
    public static List<string> Args(params string[] arguments) => [DllPath, .. arguments];
}
