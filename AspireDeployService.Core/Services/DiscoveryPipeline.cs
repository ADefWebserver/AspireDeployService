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
/// The steps Discover, Setup and Deploy share (flows §7.1 and SP §7.3): materialize the source, scan it,
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
        string workKeyDir, RunContext run, Stop stop, bool assumeTrusted, CancellationToken ct)
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
        var scan = await Task.Run(() => AppHostScanner.Scan(identity.RootFolder, source.AppHostPath, options), ct);
        await run.Log.InfoAsync($"AppHost: {scan.AppHost.AppHostPath} ({scan.AppHost.Kind}, Aspire {scan.AppHost.AspireSdkVersion}).", ct);
        foreach (var warning in scan.Warnings)
            await run.Log.InfoAsync("Warning: " + warning, ct);

        if (stop == Stop.AfterScan)
            return new PipelineResult(identity, scan, null, null);

        if (source.Kind == SourceKind.GitHub && !assumeTrusted && identity.CommitSha is { } sha
            && !trust.IsTrusted(options.WorkRoot, source.Location, sha))
        {
            var ok = await interaction.ConfirmAsync("Run code from GitHub?",
                $"This builds and runs code from {source.Location} at {sha} on this machine. Continue?", "Trust and continue", ct);
            ct.ThrowIfCancellationRequested();
            if (!ok)
                throw new DeployerException(OutcomeCode.Declined, "Stopped: the commit was not trusted, so nothing was built or run.");
            await trust.AddAsync(options.WorkRoot, source.Location, sha, ct);
        }

        var cli = await provisioner.EnsureAsync(scan.AppHost.AspireSdkVersion, options.ToolsRoot, run, ct);
        if (stop == Stop.AfterCli)
            return new PipelineResult(identity, scan, cli, null);

        var manifest = await ManifestRunner.RunAsync(cli, identity.RootFolder, scan.AppHost.AppHostPath, workKeyDir,
            environmentName, TimeSpan.FromMinutes(options.DiscoveryTimeoutMinutes), run, ct);
        return new PipelineResult(identity, scan, cli, manifest);
    }
}
