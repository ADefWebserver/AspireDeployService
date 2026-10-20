using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Sources;
using Microsoft.Extensions.Logging;

namespace AspireDeployService.Core.Runs;

public sealed record LogLine(DateTimeOffset Utc, string Stream, string Text);

/// <summary>Receives a run's redacted lines (implemented by the MAUI ActiveRun). Called from background threads.</summary>
public interface IRunObserver
{
    void OnLine(LogLine line);

    void OnRunStarted(Guid runId, string logFile);

    /// <summary>AI Help plan D11: the source is on disk at <paramref name="rootFolder"/>. A deploy calls it twice.</summary>
    void OnSourceReady(string rootFolder) { }
}

/// <summary>An observer that ignores everything, for callers that need no live output.</summary>
public sealed class NullRunObserver : IRunObserver
{
    public static NullRunObserver Instance { get; } = new();
    public void OnLine(LogLine line) { }
    public void OnRunStarted(Guid runId, string logFile) { }
}

/// <summary>Everything one run needs: its id, its own redactor, a runner that uses it, and its log (plan §6.2).</summary>
public sealed class RunContext : IAsyncDisposable
{
    internal RunContext(Guid runId, DeploySecretRedactor redactor, IProcessRunner runner, RunLogSink log, IRunObserver observer)
    {
        RunId = runId;
        Observer = observer;
        Redactor = redactor;
        Runner = runner;
        Log = log;
    }

    public Guid RunId { get; }
    public ISecretRedactor Redactor { get; }
    public IProcessRunner Runner { get; }
    public RunLogSink Log { get; }
    public IRunObserver Observer { get; }
    public string LogFile => Log.LogFile;
    public RunServices Services => new(Log, Redactor);

    public ValueTask DisposeAsync() => Log.DisposeAsync();
}

public interface IRunContextFactory
{
    /// <summary>Called by a service once it knows the work key. The log file is <c>&lt;workKeyDir&gt;/logs/&lt;utc&gt;-&lt;runId&gt;.log</c>.</summary>
    RunContext Create(string workKeyDir, IRunObserver observer);
}

public sealed class RunContextFactory(ILoggerFactory loggerFactory, TimeProvider time) : IRunContextFactory
{
    public RunContext Create(string workKeyDir, IRunObserver observer)
    {
        var runId = Guid.NewGuid();
        var redactor = new DeploySecretRedactor();
        var logFile = Path.Combine(workKeyDir, "logs", $"{time.GetUtcNow():yyyyMMddTHHmmssZ}-{runId:N}.log");
        var sink = new RunLogSink(runId, logFile, redactor, observer, time);
        var runner = new ProcessRunner(redactor, loggerFactory.CreateLogger<ProcessRunner>());
        observer.OnRunStarted(runId, logFile);
        return new RunContext(runId, redactor, runner, sink, observer);
    }
}
