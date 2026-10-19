using System.Text;
using System.Text.RegularExpressions;
using AspireDeployService.Core.Runs;

namespace AspireDeployService.Core.Logging;

/// <summary>
/// The log of one run (plan D11): every line is redacted with the run's redactor, appended to the run log
/// file (shared for reading, so it can be tailed) and handed to the observer. A file that cannot be written
/// adds one warning line and never fails the run.
/// </summary>
public sealed partial class RunLogSink : IDeploymentLogSink
{
    // The Aspire CLI colors its output; escape sequences are noise in the UI and the log file.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiEscape();

    private readonly ISecretRedactor _redactor;
    private readonly IRunObserver _observer;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private bool _fileBroken;

    public RunLogSink(Guid runId, string logFile, ISecretRedactor redactor, IRunObserver observer, TimeProvider? time = null)
    {
        DeploymentId = runId;
        LogFile = logFile;
        _redactor = redactor;
        _observer = observer;
        _time = time ?? TimeProvider.System;
    }

    public Guid DeploymentId { get; }
    public string LogFile { get; }
    public string? CurrentStep { get; set; }

    public ValueTask WriteAsync(string stream, string line, CancellationToken ct = default)
    {
        var redacted = _redactor.Redact(AnsiEscape().Replace(line, ""));
        var now = _time.GetUtcNow();
        string? warning = null;

        lock (_gate)
        {
            if (!_fileBroken)
            {
                try
                {
                    _writer ??= OpenWriter();
                    _writer.WriteLine($"{now:O} [{stream}] {redacted}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _fileBroken = true;
                    warning = $"The run log file cannot be written ({ex.Message}); output continues here only.";
                }
            }
        }

        _observer.OnLine(new LogLine(now, stream, redacted));
        if (warning is not null)
            _observer.OnLine(new LogLine(now, LogStreams.System, warning));
        return ValueTask.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch (IOException)
            {
                _fileBroken = true;
            }
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (IOException)
            {
                // Already reported as broken, or the disk vanished; nothing else to do.
            }
            _writer = null;
        }
        return ValueTask.CompletedTask;
    }

    private StreamWriter OpenWriter()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
        var stream = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }
}
