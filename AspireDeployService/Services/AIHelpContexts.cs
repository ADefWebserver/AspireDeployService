using System.Security.Cryptography;
using System.Text;
using AspireDeployService.Core.AI;
using AspireDeployService.Core.Errors;
using AspireDeployService.Core.Prerequisites;
using AspireDeployService.Core.Runs;

namespace AspireDeployService.Services;

/// <summary>
/// Builds the context for each AI Help button placement (AI Help plan §6.5). Only named, non-secret facts are added:
/// never a profile or settings object. Everything is scrubbed again before it is sent (D8).
/// </summary>
public static class AIHelpContexts
{
    public static AIHelpContext FromRun(ActiveRun run, string? page, string? extraLine = null)
    {
        var result = run.Result;
        var summary = result?.Message ?? "The run is still going.";
        if (extraLine is { Length: > 0 })
            summary += "\n" + extraLine;
        return new AIHelpContext($"run:{run.Key}:{run.StartedUtc.UtcTicks}",
            result is { Succeeded: false } ? $"{run.Title} failed" : run.Title,
            AIContextKind.RunResult, summary, RunFacts(run, page), run.Snapshot(), run.SourceRoot, run.LogFile);
    }

    public static AIHelpContext FromLog(ActiveRun run, string? page) =>
        new($"log:{run.Key}:{run.StartedUtc.UtcTicks}", $"{run.Title} log", AIContextKind.Log,
            run.Result?.Message ?? "The run is still going; this is its log so far.",
            RunFacts(run, page), run.Snapshot(), run.SourceRoot, run.LogFile);

    public static AIHelpContext FromCheck(PrerequisiteCheck check, ActiveRun? run)
    {
        var summary = new StringBuilder()
            .Append(check.Name).Append(": ").AppendLine(check.State.ToString())
            .AppendLine(check.Detail);
        if (check.Fix is { Length: > 0 } fix)
            summary.Append("Suggested fix: ").AppendLine(fix);
        var facts = new List<KeyValuePair<string, string>> { new("Page", "Setup"), new("Check", check.Name), new("State", check.State.ToString()) };
        if (run is not null)
            facts.Add(new("Scope", run.Key == "setup:general" ? "General checks" : "Profile " + run.Key["setup:".Length..]));
        return new AIHelpContext($"setup:{run?.Key ?? "general"}:{check.Name}", $"Setup check: {check.Name}",
            AIContextKind.SetupCheck, summary.ToString(), facts, [], run?.SourceRoot, run?.LogFile);
    }

    public static AIHelpContext FromWarning(string page, string text, ActiveRun? run) =>
        new($"warning:{page}:{Hash(text)}", $"Warning on {page}", AIContextKind.Warning, text,
            run is null ? [new("Page", page)] : RunFacts(run, page), [], run?.SourceRoot, run?.LogFile);

    /// <summary>The message and the page only.</summary>
    public static AIHelpContext FromError(string page, string message) =>
        new($"error:{page}:{Hash(message)}", $"Error on {page}", AIContextKind.Error, message, [new("Page", page)], [], null, null);

    private static List<KeyValuePair<string, string>> RunFacts(ActiveRun run, string? page)
    {
        var facts = new List<KeyValuePair<string, string>>();
        if (page is { Length: > 0 })
            facts.Add(new("Page", page));
        facts.Add(new("Run", run.Title));
        if (run.Result is { } result)
        {
            facts.Add(new("Result", $"{result.Code} ({(int)result.Code})"));
            if (result.Category is not (ErrorCategory.None or ErrorCategory.Unknown))
                facts.Add(new("Category", result.Category.ToString()));
        }
        if (run.SourceRoot is { } root)
            facts.Add(new("Source", root));
        facts.Add(new("File tools", run.SourceRoot is null ? "run log only" : "available (root is the source folder)"));
        return facts;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
}
