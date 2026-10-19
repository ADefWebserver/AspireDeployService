using System.Collections.Concurrent;
using AspireDeployService.Core.Logging;

namespace AspireDeployService.Tests.Support;

/// <summary>
/// A sink that just records lines. Copied from BlazorDataDeploymentWebsite.Tests/Worker/WorkerTestSupport.cs
/// (implementation plan D2).
/// </summary>
public sealed class ListLogSink : IDeploymentLogSink
{
    public ConcurrentQueue<(string Stream, string Line)> Lines { get; } = new();
    public Guid DeploymentId { get; } = Guid.NewGuid();
    public string? CurrentStep { get; set; }

    public ValueTask WriteAsync(string stream, string line, CancellationToken ct = default)
    {
        Lines.Enqueue((stream, line));
        return ValueTask.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
