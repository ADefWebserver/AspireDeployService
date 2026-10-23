using System.Net.Http;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Interaction;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;
using AspireDeployService.Core.Tools;

namespace AspireDeployService.Core.Services;

/// <summary>What the shared pipeline produced: the source, the static scan, and (unless static only) the CLI and manifest.</summary>
public sealed record PipelineResult(SourceIdentity Source, ScanResult Scan, AspireCli? Cli, ManifestContents? Manifest)
{
    public DiscoveryResult ToDiscoveryResult() => new(
        Scan.AppHost, Source,
        Manifest?.Parameters ?? [],
        Manifest?.Resources ?? [],
        [.. Scan.Warnings, .. Manifest?.Warnings ?? []],
        Manifest is not null)
    {
        UsesSubmodulesOrLfs = Scan.UsesSubmodulesOrLfs
    };
}

/// <summary>
/// The steps the New profile wizard, Setup and Deploy share (flows §7.1 and SP §7.3): materialize the source, scan it,
/// ask for trust before running GitHub code, ensure the Aspire CLI, and run the manifest. The caller holds the
/// work key lock and owns the <see cref="RunContext"/>.
/// </summary>
public sealed class DiscoveryPipeline(
    IHttpClientFactory httpFactory,
    GitHubTokenResolver tokens,
    TrustStore trust,
    IAspireCliProvisioner provisioner,
    IUserInteraction interaction)
{
    public enum Stop { AfterScan, AfterCli, AfterManifest }

    public Task<AspireCli> EnsureCliAsync(string sdkVersion, AspireDeployOptions options, RunContext run, CancellationToken ct) =>
        provisioner.EnsureAsync(sdkVersion, options.ToolsRoot, run, ct);

    public async Task<PipelineResult> RunAsync(
        SourceSpec source, string? profileGitHubToken, string environmentName, AspireDeployOptions options,
        string workKeyDir, RunContext run, Stop stop, bool assumeTrusted, CancellationToken ct,
        string? trustedCommitSha = null)
    {
        ISourceProvider provider = source.Kind switch
        {
            SourceKind.LocalFolder => new LocalFolderSource(),
            _ => new GitHubSource(httpFactory)
            {
                Token = (await tokens.ResolveAsync(profileGitHubToken, options.GitHubToken, run, ct)).Value,
                MaxDownloadBytes = options.MaxSourceDownloadMegabytes * 1024L * 1024L
            }
        };

        var identity = await Task.Run(() => provider.MaterializeAsync(source, workKeyDir, run.Services, ct), ct);
        run.Observer.OnSourceReady(identity.RootFolder);
        foreach (var leftover in await Task.Run(() => Deploy.SiblingOverlay.CleanupLeftovers(identity.RootFolder), ct))
            await run.Log.InfoAsync($"Removed a leftover temporary file from an earlier deploy: {leftover}", ct);
        var scan = await Task.Run(() => AppHostScanner.Scan(identity.RootFolder, source.AppHostPath, options), ct);
        await run.Log.InfoAsync($"AppHost: {scan.AppHost.AppHostPath} ({scan.AppHost.Kind}, Aspire {scan.AppHost.AspireSdkVersion}).", ct);
        foreach (var warning in scan.Warnings)
            await run.Log.InfoAsync("Warning: " + warning, ct);

        if (stop == Stop.AfterScan)
            return new PipelineResult(identity, scan, null, null);

        if (source.Kind == SourceKind.GitHub && !assumeTrusted && identity.CommitSha is { } sha
            && !trust.IsTrusted(options.WorkRoot, source.Location, sha))
        {
            // Wizard plan D3: the user already agreed to this exact commit on the scan result.
            if (trustedCommitSha is not null && string.Equals(trustedCommitSha, sha, StringComparison.OrdinalIgnoreCase))
            {
                await run.Log.InfoAsync($"Trusted commit {Short(sha)} from the scan result.", ct);
            }
            else
            {
                if (trustedCommitSha is not null)
                    await run.Log.InfoAsync($"GitHub returned {Short(sha)} instead of the scanned {Short(trustedCommitSha)}.", ct);
                var ok = await interaction.ConfirmAsync("Run code from GitHub?",
                    $"This builds and runs code from {source.Location} at {sha} on this machine. Continue?", "Trust and continue", ct);
                ct.ThrowIfCancellationRequested();
                if (!ok)
                    throw new DeployerException(OutcomeCode.Declined, "Stopped: the commit was not trusted, so nothing was built or run.");
            }
            await trust.AddAsync(options.WorkRoot, source.Location, sha, ct);
        }

        var cli = await provisioner.EnsureAsync(scan.AppHost.AspireSdkVersion, options.ToolsRoot, run, ct);
        if (stop == Stop.AfterCli)
            return new PipelineResult(identity, scan, cli, null);

        ManifestContents manifest;
        await using (await AcquireAppHostLockAsync(options.WorkRoot, Path.Combine(identity.RootFolder, scan.AppHost.AppHostPath), ct))
        {
            manifest = await ManifestRunner.RunAsync(cli, identity.RootFolder, scan.AppHost.AppHostPath, workKeyDir,
                environmentName, TimeSpan.FromMinutes(options.DiscoveryTimeoutMinutes), run, ct);
        }
        return new PipelineResult(identity, scan, cli, manifest);
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    internal static TimeSpan AppHostLockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary><c>&lt;WorkRoot&gt;/locks/apphost-&lt;first 12 hex of SHA-256 of the full path, lowercased&gt;.lock</c> (resource pickers plan §6.4).</summary>
    public static string AppHostLockPath(string workRoot, string appHostFullPath)
    {
        var key = Path.GetFullPath(appHostFullPath).ToLowerInvariant();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..12];
        return Path.Combine(workRoot, "locks", $"apphost-{hash}.lock");
    }

    private static async Task<FileLock> AcquireAppHostLockAsync(string workRoot, string appHostFullPath, CancellationToken ct)
    {
        try
        {
            return await FileLock.AcquireAsync(AppHostLockPath(workRoot, appHostFullPath), AppHostLockTimeout, ct);
        }
        catch (IOException ex)
        {
            throw new DeployerException(OutcomeCode.InvalidInput, "Another run is building this AppHost. Wait for it to finish.", inner: ex);
        }
    }
}
