using System.Text.Json;

namespace AspireDeployService.Core.Infrastructure;

/// <summary>
/// Source plan D7 and §6.8: the GitHub commits the developer has agreed to build and run, in
/// <c>&lt;WorkRoot&gt;/trust.jsonl</c>. Shared by profile and ad-hoc runs. Local folders are always trusted.
/// </summary>
public sealed class TrustStore
{
    private sealed record Entry(DateTimeOffset Utc, string Repository, string CommitSha);

    public static string FilePath(string workRoot) => Path.Combine(workRoot, "trust.jsonl");

    public bool IsTrusted(string workRoot, string repository, string commitSha)
    {
        var path = FilePath(workRoot);
        if (!File.Exists(path))
            return false;
        foreach (var line in ReadLinesShared(path))
        {
            try
            {
                var e = JsonSerializer.Deserialize<Entry>(line, JsonOptions);
                if (e is not null && string.Equals(e.Repository, repository, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.CommitSha, commitSha, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (JsonException)
            {
                // A damaged line never grants trust.
            }
        }
        return false;
    }

    public async Task AddAsync(string workRoot, string repository, string commitSha, CancellationToken ct)
    {
        Directory.CreateDirectory(workRoot);
        await using var _ = await FileLock.AcquireAsync(Path.Combine(workRoot, "trust.lock"), TimeSpan.FromSeconds(30), ct);
        var line = JsonSerializer.Serialize(new Entry(DateTimeOffset.UtcNow, repository, commitSha), JsonOptions);
        await File.AppendAllTextAsync(FilePath(workRoot), line + Environment.NewLine, ct);
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
                yield return line;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>Source plan §6.8: one line per run in <c>&lt;WorkKey&gt;/history.jsonl</c>. Never holds parameter values.</summary>
public static class DeployHistory
{
    public sealed record Entry(
        DateTimeOffset Utc, string Command, string Source, string? CommitSha, string? AspireVersion,
        int ExitCode, string ErrorCategory, string? LogFile, string Origin = "ui");

    public static async Task AppendAsync(string workKeyDir, Entry entry, CancellationToken ct)
    {
        Directory.CreateDirectory(workKeyDir);
        var line = JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.AppendAllTextAsync(Path.Combine(workKeyDir, "history.jsonl"), line + Environment.NewLine, ct);
    }
}
