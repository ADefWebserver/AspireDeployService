using System.Collections.Concurrent;
using AspireDeployService.Core.Runs;

namespace AspireDeployService.Tests.Support;

/// <summary>Records what a run reports.</summary>
public sealed class ListRunObserver : IRunObserver
{
    private readonly ConcurrentQueue<LogLine> _lines = new();

    public IReadOnlyList<LogLine> Lines => _lines.ToArray();
    public Guid? RunId { get; private set; }
    public string? LogFile { get; private set; }

    public void OnLine(LogLine line) => _lines.Enqueue(line);

    public void OnRunStarted(Guid runId, string logFile)
    {
        RunId = runId;
        LogFile = logFile;
    }

    public ConcurrentQueue<string> SourceRoots { get; } = new();

    public void OnSourceReady(string rootFolder) => SourceRoots.Enqueue(rootFolder);

    public bool Contains(string text) => _lines.Any(l => l.Text.Contains(text, StringComparison.Ordinal));
}
