using System.ComponentModel;
using System.IO.Enumeration;
using System.Text;
using Microsoft.Extensions.AI;

namespace AspireDeployService.Core.AI;

/// <summary>
/// The read-only tools AI Help offers the model (AI Help plan D7, §6.4): list, read and search files under the run's
/// source root, and read the run's own log. Every result is scrubbed; every refusal is returned as text, never
/// thrown, so the model can recover. One instance per send, so <see cref="RequestBudgetChars"/> resets per request.
/// </summary>
/// <param name="deniedFullPaths">The settings file plus its .lock and .tmp, so it is refused even inside the root.</param>
public sealed class AIFileTools(string? sourceRoot, string? runLogFile, IReadOnlyCollection<string> deniedFullPaths,
    IPromptRedactor redactor)
{
    public const int RequestBudgetChars = 120_000;
    public const int MaxListEntries = 200;
    public const int MaxReadLines = 400;
    public const int MaxReadChars = 32_000;
    public const int MaxSearchHits = 100;
    public const int MaxSearchFiles = 2_000;
    public const long MaxFileBytes = 1024 * 1024;
    public const string BudgetUsed = "Budget used; answer with what you have.";

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", "node_modules", ".aspire", ".azure" };

    private static readonly string[] DeniedNamePatterns =
        ["appsettings.*.json", "local.settings.json", "secrets.json", "launchSettings.json", "*.user", ".env*",
         "*.publishsettings", "*.pfx", "*.pem", "*.key", "*.snk"];

    private readonly string? _root = sourceRoot is { Length: > 0 } ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)) : null;
    private readonly HashSet<string> _denied = new(deniedFullPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
    private int _used;

    public bool HasSourceRoot => _root is not null;
    public bool HasRunLog => !string.IsNullOrEmpty(runLogFile);

    [Description("List files and folders under a path relative to the source root.")]
    public string ListFiles(
        [Description("Folder relative to the source root. Use \".\" for the root.")] string relativePath = ".",
        [Description("Optional file name wildcard such as *.csproj (* and ? only, no folders).")] string? pattern = null)
    {
        if (_root is null)
            return "Refused: no source folder for this run.";
        if (Resolve(relativePath, out var folder) is { } refusal)
            return Spend(refusal);
        if (!Directory.Exists(folder))
            return Spend($"Not found: {relativePath}");

        var output = new StringBuilder();
        var count = 0;
        foreach (var (full, isDirectory, length) in Walk(folder))
        {
            if (isDirectory ? pattern is not null : !NameMatches(Path.GetFileName(full), pattern))
                continue;
            if (++count > MaxListEntries)
            {
                output.AppendLine($"(stopped after {MaxListEntries} entries; narrow the path or pattern)");
                break;
            }
            output.AppendLine(isDirectory ? Relative(full) + "/" : $"{Relative(full)} ({length:N0} bytes)");
        }
        return Spend(count == 0 ? "(no entries)" : output.ToString());
    }

    [Description("Read lines of a text file relative to the source root.")]
    public string ReadFile(
        [Description("File path relative to the source root.")] string relativePath,
        [Description("First line to read, starting at 1.")] int startLine = 1,
        [Description("Number of lines to read, at most 400.")] int maxLines = MaxReadLines)
    {
        if (_root is null)
            return "Refused: no source folder for this run.";
        if (Resolve(relativePath, out var file) is { } refusal)
            return Spend(refusal);
        if (Directory.Exists(file))
            return Spend($"{relativePath} is a folder; use list_files.");
        if (!File.Exists(file))
            return Spend($"Not found: {relativePath}");
        if (CheckFile(file) is { } denied)
            return Spend(denied);
        return Spend(ReadLines(file, Relative(file), startLine, maxLines));
    }

    [Description("Find lines containing text in files under the source root.")]
    public string SearchFiles(
        [Description("Text to find, not case-sensitive.")] string text,
        [Description("Optional file name wildcard such as *.cs (* and ? only).")] string? glob = null)
    {
        if (_root is null)
            return "Refused: no source folder for this run.";
        if (string.IsNullOrWhiteSpace(text))
            return Spend("Give some text to search for.");

        var output = new StringBuilder();
        int hits = 0, scanned = 0;
        foreach (var (full, isDirectory, _) in Walk(_root))
        {
            if (isDirectory || !NameMatches(Path.GetFileName(full), glob) || CheckFile(full) is not null)
                continue;
            if (++scanned > MaxSearchFiles)
            {
                output.AppendLine($"(stopped after scanning {MaxSearchFiles} files; narrow the glob)");
                break;
            }
            var number = 0;
            foreach (var line in ReadAllLines(full))
            {
                number++;
                if (!line.Contains(text, StringComparison.OrdinalIgnoreCase))
                    continue;
                output.Append(Relative(full)).Append(':').Append(number).Append(": ")
                    .AppendLine(line.Length > 300 ? line[..300] + "..." : line.Trim());
                if (++hits >= MaxSearchHits)
                    break;
            }
            if (hits >= MaxSearchHits)
            {
                output.AppendLine($"(stopped after {MaxSearchHits} matches)");
                break;
            }
        }
        return Spend(hits == 0 ? "No matches." : output.ToString());
    }

    [Description("Read lines of this run's full log file.")]
    public string ReadRunLog(
        [Description("First line to read, starting at 1.")] int startLine = 1,
        [Description("Number of lines to read, at most 400.")] int maxLines = MaxReadLines)
    {
        if (string.IsNullOrEmpty(runLogFile))
            return "Refused: this run has no log file.";
        if (!File.Exists(runLogFile))
            return Spend("The run log file no longer exists.");
        return Spend(ReadLines(runLogFile, "run log", startLine, maxLines));
    }

    /// <summary>The tools whose root is set, named list_files, read_file, search_files and read_run_log.</summary>
    public IList<AITool> AsTools()
    {
        var tools = new List<AITool>();
        if (_root is not null)
        {
            tools.Add(Create((Func<string, string?, string>)ListFiles, "list_files"));
            tools.Add(Create((Func<string, int, int, string>)ReadFile, "read_file"));
            tools.Add(Create((Func<string, string?, string>)SearchFiles, "search_files"));
        }
        if (HasRunLog)
            tools.Add(Create((Func<int, int, string>)ReadRunLog, "read_run_log"));
        return tools;

        static AIFunction Create(Delegate method, string name) =>
            AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = name });
    }

    /// <summary>One line for the chat panel describing a tool call, e.g. "Read src/App/App.csproj lines 1-60".</summary>
    public static string Describe(FunctionCallContent call)
    {
        string Arg(string name, string fallback) =>
            call.Arguments?.TryGetValue(name, out var value) == true && value?.ToString() is { Length: > 0 } s ? s : fallback;

        return call.Name switch
        {
            "list_files" => $"Listed {Arg("relativePath", ".")}" + (Arg("pattern", "") is { Length: > 0 } p ? $" ({p})" : ""),
            "read_file" => $"Read {Arg("relativePath", "?")} from line {Arg("startLine", "1")}",
            "search_files" => $"Searched for \"{Arg("text", "")}\"" + (Arg("glob", "") is { Length: > 0 } g ? $" in {g}" : ""),
            "read_run_log" => $"Read the run log from line {Arg("startLine", "1")}",
            _ => $"Called {call.Name}"
        };
    }

    private string Spend(string result)
    {
        var remaining = RequestBudgetChars - _used;
        if (remaining <= 0)
            return BudgetUsed;
        var scrubbed = redactor.Scrub(result);
        if (scrubbed.Length > remaining)
            scrubbed = scrubbed[..remaining] + "\n" + BudgetUsed;
        _used += scrubbed.Length;
        return scrubbed;
    }

    /// <summary>Returns a refusal, or null with <paramref name="full"/> set when the path may be read.</summary>
    private string? Resolve(string relativePath, out string full)
    {
        full = "";
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(_root!, relativePath ?? ".")));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Refused: '{relativePath}' is not a valid path.";
        }

        if (!IsInsideRoot(full))
            return "Refused: outside the source root.";

        // Walk each segment below the root: a symlink or junction could point anywhere.
        var current = _root!;
        foreach (var segment in full[_root!.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (SkippedFolders.Contains(segment) && (current != full || Directory.Exists(current)))
                return $"Refused: '{segment}' folders are skipped.";
            try
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    return "Refused: the path goes through a symbolic link or junction.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Refused: cannot read '{relativePath}'.";
            }
        }
        return null;
    }

    private bool IsInsideRoot(string full) =>
        string.Equals(full, _root, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Deny list, size and binary checks for one file. Null when it may be read.</summary>
    private string? CheckFile(string full)
    {
        if (_denied.Contains(full))
            return "Refused: this is Aspire Deploy's settings file.";
        var name = Path.GetFileName(full);
        if (!name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)
            && DeniedNamePatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)))
            return $"Refused: {name} files can hold secrets.";
        try
        {
            var info = new FileInfo(full);
            if (info.Length > MaxFileBytes)
                return $"Refused: {name} is larger than 1 MB.";
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(8192, (int)Math.Max(0, info.Length))];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
                return $"Refused: {name} is a binary file.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Refused: cannot read {name} ({ex.Message}).";
        }
        return null;
    }

    private static string ReadLines(string file, string label, int startLine, int maxLines)
    {
        startLine = Math.Max(1, startLine);
        maxLines = Math.Clamp(maxLines, 1, MaxReadLines);
        var output = new StringBuilder();
        int number = 0, shown = 0;
        var cut = false;
        foreach (var line in ReadAllLines(file))
        {
            // Keep counting past the window so the header can give the total.
            number++;
            if (number < startLine || shown >= maxLines || cut)
                continue;
            if (output.Length + line.Length > MaxReadChars)
            {
                cut = true;
                continue;
            }
            output.Append(number).Append(": ").AppendLine(line);
            shown++;
        }
        if (startLine > number)
            return $"{label} has {number} lines; line {startLine} is past the end.";
        var header = $"{label} lines {startLine}-{startLine + shown - 1} of {number}" +
                     (cut ? " (cut at 32,000 characters)" : "") + ":\n";
        return header + output;
    }

    private static IEnumerable<string> ReadAllLines(string file)
    {
        // FileShare.ReadWrite: RunLogSink holds the live run log open for writing.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>Recursive walk that never follows a junction and never enters a skipped folder.</summary>
    private static IEnumerable<(string Full, bool IsDirectory, long Length)> Walk(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        return new FileSystemEnumerable<(string, bool, long)>(folder,
            (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, e.IsDirectory ? 0 : e.Length), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !(e.IsDirectory && SkippedFolders.Contains(e.FileName.ToString())),
            ShouldRecursePredicate = (ref FileSystemEntry e) => !SkippedFolders.Contains(e.FileName.ToString())
        };
    }

    private static bool NameMatches(string name, string? pattern) =>
        string.IsNullOrWhiteSpace(pattern) || FileSystemName.MatchesSimpleExpression(pattern.Trim(), name, ignoreCase: true);

    private string Relative(string full) => Path.GetRelativePath(_root!, full).Replace('\\', '/');
}
