using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AgentBridge.Cli;

/// <summary>
/// One item of an Args or OptionalArgs list: literal text with "{name}" placeholders. An item always becomes exactly
/// one process argument, so a value can never be split into several arguments.
/// </summary>
internal sealed class ArgTemplate
{
    private readonly List<Segment> _segments;

    private ArgTemplate(string text, List<Segment> segments)
    {
        Text = text;
        _segments = segments;
    }

    /// <summary>The template as written in the configuration.</summary>
    public string Text { get; }

    public IEnumerable<string> Placeholders => _segments.Where(s => s.IsPlaceholder).Select(s => s.Text);

    /// <summary>The first thing in the argument is a placeholder, so the value decides whether it looks like an option.</summary>
    public string? LeadingPlaceholder => _segments is [{ IsPlaceholder: true } first, ..] ? first.Text : null;

    /// <summary>Splits the template; fails on an unbalanced brace or a placeholder that is not a plain name.</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out ArgTemplate? template, out string error)
    {
        template = null;
        error = "";
        var segments = new List<Segment>();
        var literal = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '}')
            {
                error = $"'{text}' has a '}}' without a matching '{{' (literal braces are not supported)";
                return false;
            }
            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            var close = text.IndexOf('}', i + 1);
            var name = close < 0 ? "" : text[(i + 1)..close];
            if (close < 0 || name.Contains('{'))
            {
                error = $"'{text}' has a '{{' without a matching '}}' (literal braces are not supported)";
                return false;
            }
            if (!IsName(name))
            {
                error = $"'{text}' contains '{{{name}}}', which is not a placeholder; write the name of a param, e.g. {{project}}";
                return false;
            }

            if (literal.Length > 0)
            {
                segments.Add(new Segment(literal.ToString(), IsPlaceholder: false));
                literal.Clear();
            }
            segments.Add(new Segment(name, IsPlaceholder: true));
            i = close;
        }

        if (literal.Length > 0)
        {
            segments.Add(new Segment(literal.ToString(), IsPlaceholder: false));
        }
        template = new ArgTemplate(text, segments);
        return true;
    }

    public string Render(Func<string, string> valueOf)
    {
        var result = new StringBuilder();
        foreach (var segment in _segments)
        {
            result.Append(segment.IsPlaceholder ? valueOf(segment.Text) : segment.Text);
        }
        return result.ToString();
    }

    private static bool IsName(string value) =>
        value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private sealed record Segment(string Text, bool IsPlaceholder);
}
