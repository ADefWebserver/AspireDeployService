using System.Text;
using System.Text.RegularExpressions;
using AspireDeployService.Core.Runs;

namespace AspireDeployService.Core.AI;

public enum AIContextKind { RunResult, SetupCheck, Warning, Error, Log }

/// <summary>What an AI Help button belongs to (AI Help plan §6.3).</summary>
/// <param name="Id">Stable per thing helped with, e.g. "run:discover:LocalFolder:&lt;path&gt;:&lt;startedUtc&gt;" or
/// "setup:general:dotnet". Re-opening the same Id resumes the chat.</param>
/// <param name="Facts">Short non-secret pairs: page, result code, category, AppHost path, profile name.</param>
/// <param name="SourceRoot">Folder the file tools may read; null disables list_files, read_file and search_files.</param>
/// <param name="LogFile">This run's log file; null disables read_run_log.</param>
public sealed record AIHelpContext(
    string Id, string Title, AIContextKind Kind, string Summary,
    IReadOnlyList<KeyValuePair<string, string>> Facts,
    IReadOnlyList<LogLine> LogTail,
    string? SourceRoot, string? LogFile);

public static partial class AIContextFormatter
{
    public const int MaxContextChars = 24_000;
    public const int MaxSummaryChars = 8_000;
    public const int MaxEarlierProblemLines = 50;

    [GeneratedRegex("error|fail|warn", RegexOptions.IgnoreCase)]
    private static partial Regex ProblemPattern();

    /// <summary>
    /// Markdown-ish text: title, facts, summary, then the log tail in a fenced block. The tail is the last
    /// <paramref name="maxLogTailLines"/> lines plus up to 50 earlier lines that mention an error, failure or warning,
    /// then cut from the oldest line until the whole text fits <see cref="MaxContextChars"/>.
    /// </summary>
    public static string Format(AIHelpContext context, int maxLogTailLines)
    {
        var head = new StringBuilder();
        head.Append("# ").AppendLine(context.Title);
        foreach (var (key, value) in context.Facts)
            head.Append("- ").Append(key).Append(": ").AppendLine(value);
        head.AppendLine();
        head.AppendLine(CutSummary(context.Summary.Trim()));

        var lines = SelectLines(context.LogTail, Math.Max(1, maxLogTailLines));
        if (lines.Count == 0)
            return Fit(head.ToString());

        var heading = $"\n## Log tail (last {Math.Min(maxLogTailLines, context.LogTail.Count)} lines)\n```text\n";
        const string close = "```\n";
        var budget = MaxContextChars - head.Length - heading.Length - close.Length;
        var total = lines.Sum(l => l.Length + 1);
        var first = 0;
        while (first < lines.Count && total > budget)
            total -= lines[first++].Length + 1;

        var text = new StringBuilder(head.ToString()).Append(heading);
        for (var i = first; i < lines.Count; i++)
            text.Append(lines[i]).Append('\n');
        text.Append(close);
        return Fit(text.ToString());
    }

    private static List<string> SelectLines(IReadOnlyList<LogLine> log, int maxTail)
    {
        var tailStart = Math.Max(0, log.Count - maxTail);
        var earlier = new List<string>();
        for (var i = tailStart - 1; i >= 0 && earlier.Count < MaxEarlierProblemLines; i--)
        {
            if (ProblemPattern().IsMatch(log[i].Text))
                earlier.Add(FormatLine(log[i]));
        }
        earlier.Reverse();
        if (earlier.Count > 0)
            earlier.Add("...");
        for (var i = tailStart; i < log.Count; i++)
            earlier.Add(FormatLine(log[i]));
        return earlier;
    }

    private static string FormatLine(LogLine line) =>
        $"{line.Utc.ToLocalTime():HH:mm:ss.fff} [{line.Stream}] {line.Text}";

    private static string CutSummary(string summary)
    {
        if (summary.Length <= MaxSummaryChars)
            return summary;
        var half = MaxSummaryChars / 2;
        return summary[..half] + $"\n...({summary.Length - MaxSummaryChars} characters cut)...\n" + summary[^half..];
    }

    private static string Fit(string text) => text.Length <= MaxContextChars ? text : text[..MaxContextChars];
}
