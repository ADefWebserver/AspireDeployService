using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;

namespace AspireDeployService.Core.Tools;

/// <param name="Command">What to start: a full path to a pinned launcher, or "aspire" from PATH.</param>
/// <param name="Folder">The pinned tool folder to put first on PATH, or null when the PATH copy is used.</param>
public sealed record AspireCli(string Command, string? Folder, string Version);

public interface IAspireCliProvisioner
{
    /// <summary>Source plan D9: the Aspire CLI whose version equals the AppHost SDK version, installed if needed. Code 3 on failure.</summary>
    Task<AspireCli> EnsureAsync(string sdkVersion, string toolsRoot, RunContext run, CancellationToken ct);
}

public sealed class AspireCliProvisioner : IAspireCliProvisioner
{
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);
    private const string CompleteMarker = ".ads-complete";

    public async Task<AspireCli> EnsureAsync(string sdkVersion, string toolsRoot, RunContext run, CancellationToken ct)
    {
        var wanted = Normalize(sdkVersion);

        var onPath = await TryVersionOnPathAsync(run, ct);
        if (onPath is not null && Normalize(onPath) == wanted)
        {
            await run.Log.InfoAsync($"Using the Aspire CLI {onPath} from PATH.", ct);
            return new AspireCli("aspire", null, onPath);
        }

        var dir = ToolFolder(toolsRoot, sdkVersion);
        if (Installed(dir) is { } existing)
            return new AspireCli(existing, dir, sdkVersion);

        await run.Log.InfoAsync($"Installing the Aspire CLI {sdkVersion} (the AppHost's SDK version) into {dir}.", ct);
        FileLock fileLock;
        try
        {
            fileLock = await FileLock.AcquireAsync(Path.Combine(toolsRoot, ".locks", $"aspire-{sdkVersion}.lock"), TimeSpan.FromMinutes(20), ct);
        }
        catch (IOException ex)
        {
            throw new DeployerException(OutcomeCode.MissingPrerequisite, "Another install of the same Aspire CLI did not finish in 20 minutes.", inner: ex);
        }

        await using (fileLock)
        {
            if (Installed(dir) is { } raced)
                return new AspireCli(raced, dir, sdkVersion);

            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            var spec = new ProcessSpec("dotnet", ["tool", "install", "aspire.cli", "--version", sdkVersion, "--tool-path", dir], dir)
            {
                Timeout = InstallTimeout,
                Environment = new Dictionary<string, string?> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" }
            };
            ProcessResult result;
            try
            {
                result = await run.Runner.RunAsync(spec, run.Log, ct);
            }
            catch (TimeoutException ex)
            {
                throw new DeployerException(OutcomeCode.MissingPrerequisite, $"Installing the Aspire CLI {sdkVersion} timed out.", inner: ex);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new DeployerException(OutcomeCode.MissingPrerequisite, "The .NET SDK (dotnet) was not found on PATH.", inner: ex);
            }

            var launcher = AspireLauncher.FindAspire(dir);
            if (!result.Succeeded || launcher is null)
                throw new DeployerException(OutcomeCode.MissingPrerequisite,
                    $"The Aspire CLI {sdkVersion} could not be installed (dotnet exited with {result.ExitCode}). "
                    + "Preview or daily SDK builds may have no matching aspire.cli package.\n" + result.OutputTail);

            await File.WriteAllTextAsync(Path.Combine(dir, CompleteMarker), sdkVersion, ct);
            return new AspireCli(launcher, dir, sdkVersion);
        }
    }

    /// <summary>
    /// <c>&lt;ToolsRoot&gt;/aspire/&lt;version&gt;</c>. The version comes from the deployed source and this folder is deleted
    /// recursively before an install, so it must be a plain version that stays inside ToolsRoot (code 2 otherwise).
    /// </summary>
    internal static string ToolFolder(string toolsRoot, string sdkVersion)
    {
        if (!AspireVersionRules.IsStrictVersion(sdkVersion))
            throw new DeployerException(OutcomeCode.InvalidInput, $"'{sdkVersion}' is not a valid Aspire version.");
        var root = Path.GetFullPath(Path.Combine(toolsRoot, "aspire"));
        var dir = Path.GetFullPath(Path.Combine(root, sdkVersion));
        if (!dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new DeployerException(OutcomeCode.InvalidInput, $"'{sdkVersion}' would install outside the tools folder.");
        return dir;
    }

    private static string? Installed(string dir) =>
        File.Exists(Path.Combine(dir, CompleteMarker)) ? AspireLauncher.FindAspire(dir) : null;

    /// <summary>"13.6.1+3751e6" → "13.6.1"; "13.6.1-preview.1" stays as is (a preview is a different version).</summary>
    internal static string Normalize(string version) => version.Trim().Split('+')[0];

    private static async Task<string?> TryVersionOnPathAsync(RunContext run, CancellationToken ct)
    {
        try
        {
            var spec = new ProcessSpec("aspire", ["--version"], Path.GetTempPath())
            {
                CaptureStdOut = true,
                LogStdOut = false,
                Timeout = TimeSpan.FromMinutes(1)
            };
            var result = await run.Runner.RunAsync(spec, run.Log, ct);
            return result.Succeeded
                ? result.StdOut.Select(l => l.Trim()).FirstOrDefault(l => AspireVersionRules.TryParse(l, out _))
                : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            return null;
        }
    }
}
