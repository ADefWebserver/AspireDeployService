// Copied from BlazorDataDeploymentWebsite.Tests/Worker/ProcessRunnerTests.cs (implementation plan D2).
using System.Diagnostics;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Processes;
using AspireDeployService.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AspireDeployService.Tests.Copied;

[Collection("Environment")]
public class ProcessRunnerTests
{
    private static ProcessRunner Runner() => new(new SecretRedactor(), NullLogger<ProcessRunner>.Instance);

    private static ProcessSpec LongRunning() => OperatingSystem.IsWindows()
        ? new ProcessSpec("ping", ["-n", "30", "127.0.0.1"], Path.GetTempPath())
        : new ProcessSpec("sleep", ["30"], Path.GetTempPath());

    private static ProcessSpec Echo(string text) => OperatingSystem.IsWindows()
        ? new ProcessSpec("cmd", ["/c", "echo " + text], Path.GetTempPath())
        : new ProcessSpec("sh", ["-c", "echo \"$0\"", text], Path.GetTempPath());

    private static int[] ChildPids(string name) => Process.GetProcessesByName(name).Select(p => p.Id).ToArray();

    [Fact]
    public async Task Cancel_kills_a_long_running_child()
    {
        var name = OperatingSystem.IsWindows() ? "PING" : "sleep";
        var before = ChildPids(name);
        using var cts = new CancellationTokenSource();
        var log = new ListLogSink();
        var watch = Stopwatch.StartNew();

        var run = Runner().RunAsync(LongRunning(), log, cts.Token);
        await Task.Delay(1500);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");

        // The child must be gone, not merely abandoned.
        await Task.Delay(500);
        var leftovers = ChildPids(name).Except(before).ToArray();
        Assert.Empty(leftovers);
        Assert.Contains(log.Lines, l => l.Line.Contains("canceled"));
    }

    [Fact]
    public async Task Timeout_kills_the_child_and_throws_TimeoutException()
    {
        var spec = LongRunning() with { Timeout = TimeSpan.FromSeconds(1) };
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => Runner().RunAsync(spec, new ListLogSink(), CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Output_is_streamed_redacted_and_the_exit_code_returned()
    {
        var log = new ListLogSink();
        var result = await Runner().RunAsync(Echo("AccountKey=topsecretvalue123"), log, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(log.Lines, l => l.Stream == LogStreams.StdOut && l.Line.Contains("AccountKey=[REDACTED]"));
        Assert.DoesNotContain(log.Lines, l => l.Line.Contains("topsecretvalue123"));
        Assert.DoesNotContain("topsecretvalue123", result.OutputTail);
    }

    [Fact]
    public async Task Captured_stdout_is_not_logged_when_LogStdOut_is_false()
    {
        var log = new ListLogSink();
        var spec = Echo("AZURE_CONTAINER_REGISTRY_ENDPOINT=acrbdo.azurecr.io") with { CaptureStdOut = true, LogStdOut = false };

        var result = await Runner().RunAsync(spec, log, CancellationToken.None);

        Assert.Contains(result.StdOut, l => l.Contains("acrbdo.azurecr.io"));
        Assert.DoesNotContain(log.Lines, l => l.Stream == LogStreams.StdOut);
    }

    [Fact]
    public async Task Non_zero_exit_is_reported_and_EnsureSuccess_throws()
    {
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd", ["/c", "exit 3"], Path.GetTempPath())
            : new ProcessSpec("sh", ["-c", "exit 3"], Path.GetTempPath());

        var result = await Runner().RunAsync(spec, new ListLogSink(), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        var ex = Assert.Throws<ProcessFailedException>(() => result.EnsureSuccess(spec));
        Assert.Equal(3, ex.ExitCode);
    }

    [Fact]
    public async Task Worker_secrets_are_not_inherited_by_children()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__blazordatadb", "Server=x;Password=leak");
        try
        {
            var spec = OperatingSystem.IsWindows()
                ? new ProcessSpec("cmd", ["/c", "set"], Path.GetTempPath()) { CaptureStdOut = true, LogStdOut = false }
                : new ProcessSpec("env", [], Path.GetTempPath()) { CaptureStdOut = true, LogStdOut = false };

            var result = await Runner().RunAsync(spec, new ListLogSink(), CancellationToken.None);

            Assert.DoesNotContain(result.StdOut, l => l.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__blazordatadb", null);
        }
    }

    [Fact]
    public async Task Aspire_host_settings_are_not_inherited_but_explicit_values_are_kept()
    {
        // Inherited ASPIRE_CLI_PID / ASPIRE_BACKCHANNEL_PATH make the BDO AppHost wait for an Aspire CLI forever.
        Environment.SetEnvironmentVariable("ASPIRE_CLI_PID", "4242");
        try
        {
            var spec = (OperatingSystem.IsWindows()
                ? new ProcessSpec("cmd", ["/c", "set"], Path.GetTempPath())
                : new ProcessSpec("env", [], Path.GetTempPath())) with
            {
                CaptureStdOut = true,
                LogStdOut = false,
                Environment = new Dictionary<string, string?> { ["ASPIRE_CLI_TELEMETRY_OPTOUT"] = "1" }
            };

            var result = await Runner().RunAsync(spec, new ListLogSink(), CancellationToken.None);

            Assert.DoesNotContain(result.StdOut, l => l.StartsWith("ASPIRE_CLI_PID", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.StdOut, l => l.StartsWith("ASPIRE_CLI_TELEMETRY_OPTOUT=1", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPIRE_CLI_PID", null);
        }
    }

    [Fact]
    public void Echoed_command_masks_sensitive_arguments()
    {
        var spec = new ProcessSpec("/tools/azd/1.21.1/bin/azd", ["auth", "login", "--client-secret", "s3cr3t-value", "--tenant-id", "t"], ".")
        {
            SensitiveArgumentIndexes = new HashSet<int> { 3 }
        };

        var text = ProcessRunner.FormatCommand(spec);

        Assert.Equal("azd auth login --client-secret [REDACTED] --tenant-id t", text);
    }
}
