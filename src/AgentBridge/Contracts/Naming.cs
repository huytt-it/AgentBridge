using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentBridge.Contracts;

/// <summary>
/// Name rules shared by config and requests. Request ids become file and folder names,
/// so these rules also keep them safe as paths on Windows.
/// </summary>
public static partial class Naming
{
    public static bool IsValidRequestId([NotNullWhen(true)] string? value) =>
        value is not null && RequestIdPattern().IsMatch(value) && !IsReservedWindowsName(value);

    public static bool IsValidCapabilityName([NotNullWhen(true)] string? value) =>
        value is not null && CapabilityNamePattern().IsMatch(value);

    public static bool IsValidTargetName([NotNullWhen(true)] string? value) =>
        value is not null && TargetNamePattern().IsMatch(value) && !IsReservedWindowsName(value);

    /// <summary>A business domain: the first segment of its capabilities' names, e.g. "backlog".</summary>
    public static bool IsValidDomainName([NotNullWhen(true)] string? value) =>
        value is not null && SegmentPattern().IsMatch(value);

    public static bool IsValidPlaybookName([NotNullWhen(true)] string? value) =>
        value is not null && SegmentPattern().IsMatch(value);

    /// <summary>The domain a capability belongs to: the first segment of its name ("backlog.export" → "backlog").</summary>
    public static string DomainOf(string capabilityName)
    {
        var dot = capabilityName.IndexOf('.');
        return dot < 0 ? capabilityName : capabilityName[..dot];
    }

    // Letters, digits, '.', '_' and '-'; starts and ends with a letter or digit; at most 128 characters.
    // \z rather than $ so a trailing newline does not match.
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?\z")]
    private static partial Regex RequestIdPattern();

    // Lowercase dotted segments: agent.ping, backlog.export, code.business-rules.
    [GeneratedRegex(@"^[a-z][a-z0-9-]*(?:\.[a-z][a-z0-9-]*)+\z")]
    private static partial Regex CapabilityNamePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex TargetNamePattern();

    // One segment of a capability name: backlog, code, release-notes.
    [GeneratedRegex(@"^[a-z][a-z0-9-]*\z")]
    private static partial Regex SegmentPattern();

    private static bool IsReservedWindowsName(string value)
    {
        var stem = value.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && char.IsAsciiDigit(stem[3]));
    }
}
