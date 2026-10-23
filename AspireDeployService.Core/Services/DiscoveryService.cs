using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;

namespace AspireDeployService.Core.Services;

/// <param name="ProfileName">When set, the profile's work key and token are used; <paramref name="Source"/> still decides what is scanned.</param>
/// <param name="TrustedCommitSha">The commit the user agreed to on the scan result (wizard plan D3). When the resolved SHA equals it,
/// trust is recorded without asking; otherwise the usual prompt names the resolved SHA. Ignored for local folders.</param>
public sealed record DiscoverRequest(SourceSpec Source, string? ProfileName, bool StaticOnly, bool AssumeTrusted = false,
    string? TrustedCommitSha = null);

public interface IDiscoveryService
{
    /// <summary>Flow §7.1. Throws <see cref="DeployerException"/> for every refusal in that flow.</summary>
    Task<DiscoveryResult> DiscoverAsync(DiscoverRequest request, IRunObserver observer, CancellationToken ct);
}

public sealed class DiscoveryService(ProfileStore store, IRunContextFactory runs, DiscoveryPipeline pipeline) : IDiscoveryService
{
    public async Task<DiscoveryResult> DiscoverAsync(DiscoverRequest request, IRunObserver observer, CancellationToken ct)
    {
        var snapshot = await store.LoadAsync(ct);
        var options = snapshot.Options;
        DeployProfile? profile = null;
        if (request.ProfileName is { } name && !snapshot.Document.AspireDeploy.Profiles.TryGetValue(name, out profile))
            throw new DeployerException(OutcomeCode.InvalidInput, $"Profile '{name}' does not exist.");

        var workKey = WorkKeys.For(options.WorkRoot, request.ProfileName, request.Source);
        await using var workLock = WorkKeyLock.Acquire(workKey, request.ProfileName ?? request.Source.Display);
        await using var run = runs.Create(workKey, observer);

        var result = await pipeline.RunAsync(request.Source, profile?.Source.GitHubToken, profile?.EnvironmentName ?? "Production",
            options, workKey, run,
            request.StaticOnly ? DiscoveryPipeline.Stop.AfterScan : DiscoveryPipeline.Stop.AfterManifest,
            request.AssumeTrusted, ct, request.TrustedCommitSha);

        var discovery = result.ToDiscoveryResult();
        await run.Log.InfoAsync(discovery.ManifestRan
            ? $"Found {discovery.Parameters.Count} parameters and {discovery.Resources.Count} resources."
            : "Static scan finished; nothing was built or run.", ct);
        return discovery;
    }
}
