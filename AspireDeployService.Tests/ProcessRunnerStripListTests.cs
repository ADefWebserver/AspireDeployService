using System.Diagnostics;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Processes;
using AspireDeployService.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AspireDeployService.Tests;

/// <summary>Implementation plan D9: the revised list of inherited variables that never reach a child.</summary>
[Collection("Environment")]
public class ProcessRunnerStripListTests
{
    private static ProcessRunner Runner() => new(new SecretRedactor(), NullLogger<ProcessRunner>.Instance);

    private static ProcessSpec PrintEnv() => OperatingSystem.IsWindows()
        ? new ProcessSpec("cmd", ["/c", "set"], Path.GetTempPath()) { CaptureStdOut = true, LogStdOut = false }
        : new ProcessSpec("env", [], Path.GetTempPath()) { CaptureStdOut = true, LogStdOut = false };

    [Theory]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT")]
    [InlineData("ASPIRE_DASHBOARD_URL")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("GH_TOKEN")]
    [InlineData("Parameters__x")]
    [InlineData("Azure__Location")]
    [InlineData("AspireDeploy__WorkRoot")]
    [InlineData("MSBuildSDKsPath")]
    [InlineData("DOTNET_HOST_PATH")]
    public async Task Inherited_variable_is_stripped(string name)
    {
        Environment.SetEnvironmentVariable(name, "inherited-value");
        try
        {
            var result = await Runner().RunAsync(PrintEnv(), new ListLogSink(), CancellationToken.None);
            Assert.DoesNotContain(result.StdOut, l => l.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task Worker_only_prefixes_now_pass_through()
    {
        Environment.SetEnvironmentVariable("Worker__X", "kept");
        try
        {
            var result = await Runner().RunAsync(PrintEnv(), new ListLogSink(), CancellationToken.None);
            Assert.Contains(result.StdOut, l => l.Equals("Worker__X=kept", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Worker__X", null);
        }
    }

    [Fact]
    public async Task Explicit_values_win_over_the_strip_list()
    {
        var spec = PrintEnv() with
        {
            Environment = new Dictionary<string, string?> { ["ASPIRE_CONTAINER_RUNTIME"] = "podman", ["Parameters__apiKey"] = "abc" }
        };
        var result = await Runner().RunAsync(spec, new ListLogSink(), CancellationToken.None);
        Assert.Contains("ASPIRE_CONTAINER_RUNTIME=podman", result.StdOut);
        Assert.Contains("Parameters__apiKey=abc", result.StdOut);
    }
}

/// <summary>Implementation plan D15 (1): children die when the job handle closes.</summary>
public class ChildProcessJobTests
{
    [Fact]
    public async Task Closing_the_job_kills_its_processes()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var job = new ChildProcessJob();
        Assert.True(job.IsActive);

        var psi = new ProcessStartInfo("ping", ["-n", "300", "127.0.0.1"]) { CreateNoWindow = true, UseShellExecute = false };
        using var process = Process.Start(psi)!;
        Assert.True(job.TryAssign(process));

        job.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.HasExited);
    }
}
