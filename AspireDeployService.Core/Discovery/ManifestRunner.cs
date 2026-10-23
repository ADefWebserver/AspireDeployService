using System.ComponentModel;
using AspireDeployService.Core.Errors;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Tools;

namespace AspireDeployService.Core.Discovery;

/// <summary>
/// Discovery stage 2 (source plan §6.5): <c>aspire do publish-manifest</c>. It builds and runs the AppHost but
/// gets no Azure settings and no parameter values, so it never touches Azure. Aspire 13.6.1 does not prompt for
/// missing parameters under <c>--non-interactive</c> (checked while implementing T9).
/// </summary>
public static class ManifestRunner
{
    public static async Task<ManifestContents> RunAsync(
        AspireCli cli, string sourceRoot, string appHostPath, string workKeyDir, string environmentName,
        TimeSpan timeout, RunContext run, CancellationToken ct)
    {
        // The manifest's bicep modules land next to it, so the folder is recreated each time.
        var outputDir = Path.Combine(workKeyDir, "discovery");
        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);
        var manifestPath = Path.Combine(outputDir, "aspire-manifest.json");

        var spec = new ProcessSpec(cli.Command,
            ["do", "publish-manifest", "--apphost", appHostPath, "--output-path", manifestPath, "--non-interactive", "--nologo"],
            sourceRoot)
        {
            Timeout = timeout,
            Environment = new Dictionary<string, string?>
            {
                ["DOTNET_ENVIRONMENT"] = environmentName,
                ["ASPIRE_CLI_TELEMETRY_OPTOUT"] = "1",
                ["PATH"] = ToolPath.With(cli.Folder)
            }
        };

        ProcessResult result;
        try
        {
            result = await run.Runner.RunAsync(spec, run.Log, ct);
        }
        catch (TimeoutException ex)
        {
            throw new DeployerException(OutcomeCode.DiscoveryFailed,
                $"Discovery did not finish within {timeout.TotalMinutes:0} minutes.", ErrorCategory.Timeout, ex);
        }
        catch (Win32Exception ex)
        {
            throw new DeployerException(OutcomeCode.MissingPrerequisite, "The Aspire CLI could not be started.", inner: ex);
        }

        if (!result.Succeeded || !File.Exists(manifestPath))
            throw new DeployerException(OutcomeCode.DiscoveryFailed,
                $"Discovery failed: aspire do publish-manifest exited with {result.ExitCode}. The end of its output:\n{Tail(result.OutputTail)}");

        return ManifestParser.Parse(await File.ReadAllTextAsync(manifestPath, ct), Path.GetDirectoryName(manifestPath));
    }

    private static string Tail(string text) => string.Join('\n', text.Split('\n').TakeLast(20));
}
