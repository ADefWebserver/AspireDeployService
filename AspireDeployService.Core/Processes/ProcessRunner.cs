using System.Diagnostics;
using System.Text;
using AspireDeployService.Core.Logging;
using Microsoft.Extensions.Logging;

// Copied from BlazorDataDeploymentWebsite.DeployWorker/Processes/ProcessRunner.cs (implementation plan D2).
// Changes: namespace, the stripped-variable list (D9), Job Object assignment (D15) and a fresh PATH.
namespace AspireDeployService.Core.Processes;

/// <summary>One child process invocation. Arguments are passed one by one (never concatenated into a command line).</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>Overrides for the child environment; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>The process tree is killed and a <see cref="TimeoutException"/> thrown after this long.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Argument indexes whose values are masked when the command is echoed to the log.</summary>
    public IReadOnlySet<int> SensitiveArgumentIndexes { get; init; } = new HashSet<int>();

    /// <summary>When false, stdout is captured (for parsing) but not written to the deployment log.</summary>
    public bool LogStdOut { get; init; } = true;

    /// <summary>Keep raw stdout lines in <see cref="ProcessResult.StdOut"/> (for commands whose output is parsed).</summary>
    public bool CaptureStdOut { get; init; }

    public string DisplayName => Path.GetFileNameWithoutExtension(FileName);
}

/// <param name="StdOut">Raw stdout lines, only when <see cref="ProcessSpec.CaptureStdOut"/> is set. Never logged by the runner.</param>
/// <param name="OutputTail">The last redacted stdout/stderr lines, for error classification and messages.</param>
public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> StdOut, string OutputTail)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>A child process exited non-zero. <see cref="OutputTail"/> is already redacted.</summary>
public sealed class ProcessFailedException(string command, int exitCode, string outputTail)
    : Exception($"{command} exited with code {exitCode}.")
{
    public string Command { get; } = command;
    public int ExitCode { get; } = exitCode;
    public string OutputTail { get; } = outputTail;
}

public interface IProcessRunner
{
    /// <summary>Runs the process, streaming redacted output to <paramref name="log"/>. Does not throw on a non-zero exit.</summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, IDeploymentLogSink log, CancellationToken ct);
}

/// <summary>
/// Runs aspire / az / dotnet / docker. stdout and stderr are read line by line, redacted and sent
/// to the run log; cancellation or the timeout kills the entire process tree.
/// </summary>
public sealed class ProcessRunner(ISecretRedactor redactor, ILogger<ProcessRunner> logger) : IProcessRunner
{
    private const int TailLines = 200;

    /// <summary>How long to wait for the output pipes to close after the process exited.</summary>
    internal static TimeSpan OutputDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // Plan D9. This app's own settings and inherited Azure/parameter values must not reach the target
    // AppHost (an inherited Parameters__x would override a profile value of "" = "AppHost supplies it").
    // Stray service-principal variables must not win over the developer's az login.
    // ASPIRE_/DCP_/OTEL_ come from an Aspire host running this app: with ASPIRE_CLI_PID and
    // ASPIRE_BACKCHANNEL_PATH inherited, a child AppHost waits forever for a CLI that never connects.
    // The MSBuild variables point at Visual Studio's MSBuild and break nested builds that pin another SDK.
    internal static readonly string[] StrippedPrefixes =
    [
        "ConnectionStrings__", "GitHub__", "AspireDeploy__", "Azure__", "Parameters__",
        "GITHUB_TOKEN", "GH_TOKEN",
        "IDENTITY_", "MSI_", "AZURE_TOKEN_CREDENTIALS", "AZURE_CLIENT_SECRET", "AZURE_CLIENT_CERTIFICATE",
        "AZURE_FEDERATED_TOKEN", "AZURE_OIDC_", "OTEL_", "APPLICATIONINSIGHTS_", "ASPIRE_", "DCP_",
        "MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "DOTNET_HOST_PATH"
    ];

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IDeploymentLogSink log, CancellationToken ct)
    {
        // The app's own PATH is captured at start-up; read the current one so freshly installed tools are found.
        var path = spec.Environment.GetValueOrDefault("PATH") ?? ToolPath.Current();
        var fileName = ResolveExecutable(spec.FileName, path);
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);

        foreach (var key in psi.Environment.Keys.ToList())
        {
            if (StrippedPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                psi.Environment.Remove(key);
        }
        psi.Environment["PATH"] = path;
        foreach (var (key, value) in spec.Environment)
        {
            if (value is null)
                psi.Environment.Remove(key);
            else
                psi.Environment[key] = value;
        }

        await log.WriteAsync(LogStreams.System, redactor.Redact("$ " + FormatCommand(spec)), ct);

        using var timeoutCts = spec.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {spec.DisplayName}.");
        ChildProcessJob.Shared.TryAssign(process, logger); // the tree dies with this app (D15)
        process.StandardInput.Close(); // tools must never wait for console input

        var tail = new Queue<string>();
        var stdout = new List<string>();
        var tailLock = new object();

        void AddTail(string line)
        {
            lock (tailLock)
            {
                tail.Enqueue(line);
                if (tail.Count > TailLines)
                    tail.Dequeue();
            }
        }

        async Task PumpAsync(StreamReader reader, string stream)
        {
            var scope = redactor.CreateScope();
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (stream == LogStreams.StdOut && spec.CaptureStdOut)
                {
                    lock (stdout)
                        stdout.Add(line);
                }

                var redacted = scope.Redact(line);
                if (redacted is null)
                    continue;
                AddTail(redacted);
                if (stream == LogStreams.StdErr || spec.LogStdOut)
                    await log.WriteAsync(stream, redacted, CancellationToken.None);
            }
        }

        var outTask = PumpAsync(process.StandardOutput, LogStreams.StdOut);
        var errTask = PumpAsync(process.StandardError, LogStreams.StdErr);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process, spec.DisplayName);
            await WaitQuietlyAsync(process, outTask, errTask);

            if (ct.IsCancellationRequested)
            {
                await log.WriteAsync(LogStreams.System, $"Stopped {spec.DisplayName} (canceled).", CancellationToken.None);
                throw new OperationCanceledException($"{spec.DisplayName} was canceled.", ct);
            }

            await log.WriteAsync(LogStreams.System, $"Stopped {spec.DisplayName}: no result after {spec.Timeout?.TotalMinutes:0.#} minutes.", CancellationToken.None);
            throw new TimeoutException($"{spec.DisplayName} did not finish within {spec.Timeout?.TotalMinutes:0.#} minutes.");
        }

        // A grandchild (MSBuild node, build server, DCP) can inherit the output pipes and keep them open after the
        // tool itself exited; wait a bounded time, and stay cancelable, instead of hanging the run forever.
        using (var drain = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            drain.CancelAfter(OutputDrainTimeout);
            try
            {
                await Task.WhenAll(outTask, errTask).WaitAsync(drain.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await log.WriteAsync(LogStreams.System,
                    $"{spec.DisplayName} exited, but a child process still holds its output open; continuing without it.", CancellationToken.None);
            }
        }

        string tailText;
        lock (tailLock)
            tailText = string.Join('\n', tail);
        return new ProcessResult(process.ExitCode, stdout, tailText);
    }

    /// <summary>The echoed command line with sensitive values masked. Display only; never executed.</summary>
    public static string FormatCommand(ProcessSpec spec)
    {
        var parts = new List<string> { spec.DisplayName };
        for (var i = 0; i < spec.Arguments.Count; i++)
        {
            var arg = spec.Arguments[i];
            if (spec.SensitiveArgumentIndexes.Contains(i))
                arg = SecretRedactor.Mask;
            else if (arg.Length == 0 || arg.Any(char.IsWhiteSpace) || arg.Contains('"'))
                arg = "\"" + arg.Replace("\"", "\\\"") + "\"";
            parts.Add(arg);
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Finds <paramref name="name"/> on the child's PATH (pinned tool folders come first), so a
    /// pinned Aspire CLI wins over the one on the developer's PATH. Windows also tries PATHEXT.
    /// </summary>
    public static string ResolveExecutable(string name, string? pathOverride)
    {
        if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return name;

        var path = pathOverride ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] extensions = OperatingSystem.IsWindows()
            ? ["", ".exe", ".cmd", ".bat"]
            : [""];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate) && (ext.Length > 0 || !OperatingSystem.IsWindows()))
                    return candidate;
            }
        }
        return name;
    }

    private void Kill(Process process, string name)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Killing {Process} failed.", name);
        }
    }

    private static async Task WaitQuietlyAsync(Process process, Task outTask, Task errTask)
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(grace.Token);
            await Task.WhenAll(outTask, errTask).WaitAsync(grace.Token);
        }
        catch
        {
            // Output pumps of a killed tree can stay open if a grandchild inherited the pipe; do not wait forever.
        }
    }
}

public static class ProcessResultExtensions
{
    /// <summary>Throws <see cref="ProcessFailedException"/> for a non-zero exit.</summary>
    public static ProcessResult EnsureSuccess(this ProcessResult result, ProcessSpec spec) =>
        result.Succeeded ? result : throw new ProcessFailedException(ProcessRunner.FormatCommand(spec), result.ExitCode, result.OutputTail);
}
