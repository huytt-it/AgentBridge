using System.Reflection;

namespace AgentBridge;

public static class AgentVersion
{
    /// <summary>Version of this build, e.g. "0.1.0+3f2a1c9" when built from a git checkout.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var version = typeof(AgentVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // Keep a short commit hash instead of the full 40 characters.
        var plus = version.IndexOf('+');
        return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }
}
