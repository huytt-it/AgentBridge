using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentBridge.TestTools;

/// <summary>
/// A fake command-line tool for the cli capability tests. The first argument picks the scenario:
///   backlog --project P [--status S] [--out FILE]   fake backlog as Markdown, to the file or to stdout
///   echo-args ...                                   the received arguments as a JSON array
///   json                                            a JSON object
///   json-big KB                                     a JSON object holding about that many KB
///   fail CODE MESSAGE                               MESSAGE on stderr, exit code CODE
///   sleep SECONDS                                   waits
///   spawn-and-sleep SECONDS                         starts a child copy of this tool that sleeps, prints its PID, sleeps
///   env NAME                                        the value of an environment variable
///   big KB                                          that many KB of text, numbered lines
///   read-stdin                                      reads stdin to the end, then prints "eof"
/// Text is always written as UTF-8 without a byte order mark, whatever the console code page is.
/// </summary>
internal static class CliFixture
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly (string Id, string Epic, string Title, string Status, string Priority)[] Backlog =
    [
        ("BL-101", "Đăng nhập và phân quyền", "Cho phép đăng nhập bằng tài khoản công ty", "Open", "High"),
        ("BL-102", "Đăng nhập và phân quyền", "Phân quyền theo phòng ban", "InProgress", "Medium"),
        ("BL-201", "Báo cáo doanh thu", "Xuất báo cáo doanh thu theo tháng ra Excel", "Open", "High"),
        ("BL-202", "Báo cáo doanh thu", "Biểu đồ so sánh doanh thu cùng kỳ năm trước", "Done", "Low"),
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true, NewLine = "\n" };
        using var stderr = new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true, NewLine = "\n" };

        switch (args)
        {
            case ["backlog", .. var rest]:
                return await BacklogAsync(rest, stdout, stderr);
            case ["echo-args", .. var rest]:
                stdout.WriteLine(ToJsonArray(rest));
                return 0;
            case ["json"]:
                stdout.WriteLine("""{"project":"DEMO","count":3,"items":["BL-101","BL-102","BL-201"],"note":"Tiếng Việt có dấu"}""");
                return 0;
            case ["json-big", var kb]:
                JsonBig(int.Parse(kb, CultureInfo.InvariantCulture), stdout);
                return 0;
            case ["fail", var code, var message]:
                stderr.WriteLine(message);
                return int.Parse(code, CultureInfo.InvariantCulture);
            case ["sleep", var seconds]:
                await Task.Delay(TimeSpan.FromSeconds(double.Parse(seconds, CultureInfo.InvariantCulture)));
                return 0;
            case ["spawn-and-sleep", var seconds]:
                return await SpawnAndSleepAsync(seconds, stdout);
            case ["env", var name]:
                stdout.WriteLine(Environment.GetEnvironmentVariable(name) ?? "");
                return 0;
            case ["big", var kb]:
                Big(int.Parse(kb, CultureInfo.InvariantCulture), stdout);
                return 0;
            case ["read-stdin"]:
                await Console.OpenStandardInput().CopyToAsync(Stream.Null);
                stdout.WriteLine("eof");
                return 0;
            default:
                stderr.WriteLine($"Unknown scenario or arguments: {string.Join(" ", args)}");
                return 2;
        }
    }

    private static async Task<int> BacklogAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? project = null, status = null, outFile = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length || args[i] is not ("--project" or "--status" or "--out"))
            {
                stderr.WriteLine($"Usage: backlog --project <p> [--status <s>] [--out <file>] (got: {string.Join(" ", args)})");
                return 2;
            }
            var value = args[++i];
            switch (args[i - 1])
            {
                case "--project": project = value; break;
                case "--status": status = value; break;
                default: outFile = value; break;
            }
        }
        if (project is null)
        {
            stderr.WriteLine("--project is required");
            return 2;
        }

        var markdown = new StringBuilder();
        markdown.Append($"# Backlog: {project}\n\n");
        markdown.Append($"Bộ lọc trạng thái: {status ?? "tất cả"}\n");
        foreach (var epic in Backlog.Where(b => status is null || b.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).GroupBy(b => b.Epic))
        {
            markdown.Append($"\n## {epic.Key}\n\n");
            foreach (var item in epic)
            {
                markdown.Append($"- **{item.Id}** {item.Title} ({item.Status}, {item.Priority})\n");
            }
        }

        if (outFile is null)
        {
            stdout.Write(markdown.ToString());
        }
        else
        {
            await File.WriteAllTextAsync(outFile, markdown.ToString(), Utf8);
        }
        return 0;
    }

    private static async Task<int> SpawnAndSleepAsync(string seconds, TextWriter stdout)
    {
        // Started through the same host that runs this tool (dotnet AgentBridge.TestTools.dll ... or the apphost).
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        {
            startInfo.ArgumentList.Add(typeof(CliFixture).Assembly.Location);
        }
        startInfo.ArgumentList.Add("cli");
        startInfo.ArgumentList.Add("sleep");
        startInfo.ArgumentList.Add(seconds);

        using var child = Process.Start(startInfo)!;
        stdout.WriteLine(child.Id.ToString(CultureInfo.InvariantCulture));
        await Task.Delay(TimeSpan.FromSeconds(double.Parse(seconds, CultureInfo.InvariantCulture)));
        return 0;
    }

    /// <summary>
    /// Written by hand: the tools run on the .NET 8 runtime even when the tests run on .NET 10, where the newer
    /// System.Text.Json that the project references is not copied next to the tests.
    /// </summary>
    private static string ToJsonArray(string[] values)
    {
        var json = new StringBuilder("[");
        for (var i = 0; i < values.Length; i++)
        {
            json.Append(i == 0 ? "\"" : ",\"");
            foreach (var c in values[i])
            {
                switch (c)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case < ' ': json.Append($"\\u{(int)c:x4}"); break;
                    default: json.Append(c); break;
                }
            }
            json.Append('"');
        }
        return json.Append(']').ToString();
    }

    private static void JsonBig(int kilobytes, TextWriter stdout)
    {
        stdout.Write("{\"items\":[");
        for (var i = 0; i < kilobytes; i++)
        {
            stdout.Write(i == 0 ? "\"" : ",\"");
            stdout.Write(new string('x', 1000));
            stdout.Write('"');
        }
        stdout.WriteLine("]}");
    }

    private static void Big(int kilobytes, TextWriter stdout)
    {
        // Numbered lines of exactly 1 KB, so a test can tell from the numbers which part of the output it got.
        for (var i = 1; i <= kilobytes; i++)
        {
            stdout.Write($"line-{i:D6} ".PadRight(1023, 'x'));
            stdout.Write('\n');
        }
    }
}
