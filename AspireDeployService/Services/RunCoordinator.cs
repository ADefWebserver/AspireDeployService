using AspireDeployService.Core.Errors;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Runs;

namespace AspireDeployService.Services;

/// <summary>
/// Plan D11 and D14: owns every run, so leaving a page never orphans one. Work runs on the thread pool, never on
/// the UI dispatcher, and every exception becomes an <see cref="OperationResult"/> for the banner.
/// </summary>
public sealed class RunCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveRun> _runs = new(StringComparer.Ordinal);

    /// <summary>Raised (on any thread) when a run starts or finishes.</summary>
    public event Action? RunsChanged;

    public bool HasActive
    {
        get
        {
            lock (_gate)
                return _runs.Values.Any(r => r.IsRunning);
        }
    }

    public IReadOnlyList<ActiveRun> Active
    {
        get
        {
            lock (_gate)
                return _runs.Values.Where(r => r.IsRunning).ToList();
        }
    }

    /// <summary>The run with this key, running or the last finished one, so a page can re-attach.</summary>
    public ActiveRun? TryGet(string key)
    {
        lock (_gate)
            return _runs.GetValueOrDefault(key);
    }

    /// <summary>The most recently started run whose key starts with <paramref name="prefix"/>, to re-attach a page.</summary>
    public ActiveRun? Latest(string prefix)
    {
        lock (_gate)
            return _runs.Values.Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal)).MaxBy(r => r.StartedUtc);
    }

    /// <summary>Starts work that returns a value (for example a DiscoveryResult); <paramref name="toResult"/> turns it into the banner result.</summary>
    /// <remarks>
    /// If a run with <paramref name="key"/> is already running, that run is returned unchanged and <paramref name="work"/>
    /// is not started; <paramref name="tag"/> is set only on a new run, so a page can tell which input is really running.
    /// </remarks>
    public ActiveRun Start<T>(string key, string title, Func<IRunObserver, CancellationToken, Task<T>> work, Func<T, OperationResult> toResult,
        object? tag = null)
    {
        ActiveRun run;
        lock (_gate)
        {
            if (_runs.TryGetValue(key, out var existing) && existing.IsRunning)
                return existing;
            run = new ActiveRun(key, title) { Tag = tag };
            _runs[key] = run;
        }
        RunsChanged?.Invoke();

        run.Completion = Task.Run(async () =>
        {
            try
            {
                var value = await work(run, run.Token);
                run.Finish(toResult(value), value);
            }
            catch (DeployerException ex)
            {
                run.Finish(new OperationResult(ex.Code, ex.Message, ex.Category, run.LogFile), null);
            }
            catch (OperationCanceledException)
            {
                run.Finish(new OperationResult(OutcomeCode.DeployFailed, "Stopped.", ErrorCategory.Canceled, run.LogFile), null);
            }
            catch (Exception ex)
            {
                run.Finish(new OperationResult(OutcomeCode.Unexpected, $"Unexpected error: {ex.Message}", ErrorCategory.Unknown, run.LogFile), null);
            }
            finally
            {
                RunsChanged?.Invoke();
            }
        });
        return run;
    }

    public ActiveRun Start(string key, string title, Func<IRunObserver, CancellationToken, Task<OperationResult>> work, object? tag = null) =>
        Start(key, title, work, r => r, tag);

    /// <summary>Plan D15 (2): cancels every running run and waits up to <paramref name="timeout"/> for them to stop.</summary>
    public async Task CancelAllAsync(TimeSpan timeout)
    {
        var running = Active;
        foreach (var run in running)
            run.Cancel();
        try
        {
            await Task.WhenAll(running.Select(r => r.Completion)).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // The Job Object kills whatever is left when the app exits.
        }
    }
}

/// <summary>One run: its log ring buffer, cancellation and result. Lines arrive on background threads.</summary>
public sealed class ActiveRun : IRunObserver
{
    public const int MaxLines = 5000;

    private readonly object _gate = new();
    private readonly Queue<LogLine> _lines = new();
    private readonly CancellationTokenSource _cts = new();
    private long _version;

    internal ActiveRun(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }
    public string Title { get; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public string? LogFile { get; private set; }
    public bool IsRunning => Result is null;
    public OperationResult? Result { get; private set; }
    public object? Value { get; private set; }
    public Task Completion { get; internal set; } = Task.CompletedTask;
    public CancellationToken Token => _cts.Token;
    public bool CancelRequested => _cts.IsCancellationRequested;

    /// <summary>Changes whenever a line is added or the run ends; LogViewer polls it.</summary>
    public long Version => Interlocked.Read(ref _version);

    public T? ValueAs<T>() where T : class => Value as T;

    /// <summary>Whatever the page needs to restore its form when it re-attaches (for example the request).</summary>
    public object? Tag { get; set; }

    public void Cancel()
    {
        if (IsRunning)
            _cts.Cancel();
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate)
            return _lines.ToArray();
    }

    void IRunObserver.OnLine(LogLine line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines)
                _lines.Dequeue();
        }
        Interlocked.Increment(ref _version);
    }

    void IRunObserver.OnRunStarted(Guid runId, string logFile) => LogFile ??= logFile;

    internal void Finish(OperationResult result, object? value)
    {
        Value = value;
        Result = result with { LogFile = result.LogFile ?? LogFile };
        Interlocked.Increment(ref _version);
    }
}
