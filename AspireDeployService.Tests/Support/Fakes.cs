using System.Collections.Concurrent;
using System.Net;
using AspireDeployService.Core.Interaction;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;

namespace AspireDeployService.Tests.Support;

/// <summary>Answers process runs from a handler and records every spec. Output goes through the run's sink.</summary>
public sealed class FakeProcessRunner(Func<ProcessSpec, CancellationToken, Task<ProcessResult>> handler) : IProcessRunner
{
    public ConcurrentQueue<ProcessSpec> Specs { get; } = new();

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IDeploymentLogSink log, CancellationToken ct)
    {
        Specs.Enqueue(spec);
        await log.WriteAsync(LogStreams.System, "$ " + ProcessRunner.FormatCommand(spec), ct);
        var result = await handler(spec, ct);
        if (spec.LogStdOut)
        {
            foreach (var line in result.StdOut)
                await log.WriteAsync(LogStreams.StdOut, line, ct);
        }
        return result;
    }

    public static ProcessResult Ok(params string[] stdout) => new(0, stdout, string.Join('\n', stdout));
    public static ProcessResult Fail(int code, string tail) => new(code, [], tail);

    public static bool Is(ProcessSpec s, params string[] args) =>
        s.Arguments.Count >= args.Length && args.Select((a, i) => s.Arguments[i] == a).All(x => x);
}

public sealed class FakeRunContextFactory(FakeProcessRunner runner) : IRunContextFactory
{
    public List<RunContext> Created { get; } = [];

    public RunContext Create(string workKeyDir, IRunObserver observer)
    {
        var redactor = new DeploySecretRedactor();
        var runId = Guid.NewGuid();
        var file = Path.Combine(workKeyDir, "logs", runId.ToString("N") + ".log");
        var context = new RunContext(runId, redactor, runner, new RunLogSink(runId, file, redactor, observer));
        observer.OnRunStarted(runId, file);
        Created.Add(context);
        return context;
    }
}

public sealed class FakeUserInteraction(bool answer = true) : IUserInteraction
{
    public ConcurrentQueue<(string Title, string Message)> Asked { get; } = new();

    /// <summary>When set, the question stays open until the token is canceled.</summary>
    public bool WaitForCancel { get; init; }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken ct)
    {
        Asked.Enqueue((title, message));
        if (WaitForCancel)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return answer;
    }
}

/// <summary>Returns canned HTTP responses by URL prefix and records requests (with their Authorization header).</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly List<(string Prefix, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public ConcurrentQueue<(string Url, string? Authorization)> Requests { get; } = new();

    public StubHttpHandler On(string urlPrefix, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((urlPrefix, respond));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requests.Enqueue((url, request.Headers.Authorization?.ToString()));
        var route = _routes.FirstOrDefault(r => url.StartsWith(r.Prefix, StringComparison.Ordinal));
        return Task.FromResult(route.Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
