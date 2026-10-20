using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Prerequisites;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Core.Services;

public interface ISetupService
{
    /// <summary>Source plan §6.6. With a profile, it also materializes the source and checks what its AppHost needs.</summary>
    Task<IReadOnlyList<PrerequisiteCheck>> CheckAsync(string? profileName, IRunObserver observer, CancellationToken ct);
}

public sealed class SetupService(ProfileStore store, IRunContextFactory runs, PrerequisiteChecker checker, DiscoveryPipeline pipeline) : ISetupService
{
    public async Task<IReadOnlyList<PrerequisiteCheck>> CheckAsync(string? profileName, IRunObserver observer, CancellationToken ct)
    {
        var snapshot = await store.LoadAsync(ct);
        var options = snapshot.Options;
        var checks = new List<PrerequisiteCheck>();

        if (profileName is null)
        {
            await using var run = runs.Create(WorkKeys.For(options.WorkRoot, null, null), observer);
            checks.Add(await checker.DotnetSdkAsync(run, null, ct));
            checks.Add(await checker.AzureSignInAsync(run, null, null, ct));
            checks.Add(await checker.ContainerRuntimeAsync(run, null, required: false, ct));
            checks.Add(await checker.NodeAsync(run, required: false, ct));
            checks.Add(await checker.GitHubCliAsync(run, ct));
            checks.Add(checker.LongPaths(null));
            return checks;
        }

        if (!snapshot.Document.AspireDeploy.Profiles.TryGetValue(profileName, out var profile))
            throw new DeployerException(OutcomeCode.InvalidInput, $"Profile '{profileName}' does not exist.");

        var workKey = WorkKeys.For(options.WorkRoot, profileName, null);
        await using var workLock = WorkKeyLock.Acquire(workKey, profileName);
        await using var profileRun = runs.Create(workKey, observer);

        // Scan without running the AppHost; installing the matching Aspire CLI runs no source code.
        var result = await pipeline.RunAsync(profile.ToSourceSpec(), profile.Source.GitHubToken, profile.EnvironmentName,
            options, workKey, profileRun, DiscoveryPipeline.Stop.AfterScan, assumeTrusted: false, ct);
        var appHost = result.Scan.AppHost;

        checks.Add(await checker.DotnetSdkAsync(profileRun, appHost.DotnetSdkFromGlobalJson, ct));
        checks.Add(await checker.AzureSignInAsync(profileRun, profile.Destination.SubscriptionId, profile.Destination.TenantId, ct));
        // Project resources are built into images for Azure compute (assumed; plan T0b (d)), so a C# AppHost needs a runtime.
        checks.Add(await checker.ContainerRuntimeAsync(profileRun, profile.ContainerRuntime, required: true, ct));
        if (appHost.Kind == AppHostKind.TypeScript)
            checks.Add(await checker.NodeAsync(profileRun, required: true, ct));
        checks.Add(await AspireCliCheckAsync(appHost.AspireSdkVersion, options, profileRun, ct));
        checks.Add(appHost.HasAzureTarget
            ? new PrerequisiteCheck("Azure compute environment", CheckState.Pass, string.Join(", ", appHost.DeclaredTargets))
            : new PrerequisiteCheck("Azure compute environment", CheckState.Fail, "The AppHost declares no Azure compute environment.",
                "Add builder.AddAzureContainerAppEnvironment(\"env\"); with the Aspire.Hosting.Azure.AppContainers package."));
        if (result.Scan.UsesSubmodulesOrLfs && profile.Source.Kind == Sources.SourceKind.GitHub)
            checks.Add(new PrerequisiteCheck("Complete source", CheckState.Fail, "The repository uses submodules or Git LFS.",
                "Clone it and use a local folder source."));
        checks.Add(checker.LongPaths(result.Source.RootFolder));
        return checks;
    }

    private async Task<PrerequisiteCheck> AspireCliCheckAsync(string version, AspireDeployOptions options, RunContext run, CancellationToken ct)
    {
        try
        {
            var cli = await pipeline.EnsureCliAsync(version, options, run, ct);
            return new PrerequisiteCheck("Aspire CLI", CheckState.Pass, cli.Folder is null ? $"{version} on PATH." : $"{version} in {cli.Folder}.");
        }
        catch (DeployerException ex)
        {
            return new PrerequisiteCheck("Aspire CLI", CheckState.Fail, ex.Message, "Check the .NET SDK and network access to nuget.org.");
        }
    }
}
