// Copied from BlazorDataDeploymentWebsite (implementation plan D2): DeployWorker/Logging/DeploymentLogSink.cs; IDeploymentLogSinkFactory dropped.
namespace AspireDeployService.Core.Logging;

public static class LogStreams
{
    public const string StdOut = "stdout";
    public const string StdErr = "stderr";
    public const string System = "system";
}

/// <summary>
/// The streamed log of one run. DeploymentId is the run id. Implementations redact every line, so
/// callers may pass raw text; <see cref="FlushAsync"/> makes everything written so far visible.
/// </summary>
public interface IDeploymentLogSink : IAsyncDisposable
{
    Guid DeploymentId { get; }

    /// <summary>The pipeline step the following lines belong to (stored with each row).</summary>
    string? CurrentStep { get; set; }

    ValueTask WriteAsync(string stream, string line, CancellationToken ct = default);

    Task FlushAsync(CancellationToken ct = default);
}

public static class DeploymentLogSinkExtensions
{
    public static ValueTask InfoAsync(this IDeploymentLogSink log, string line, CancellationToken ct = default) =>
        log.WriteAsync(LogStreams.System, line, ct);
}
