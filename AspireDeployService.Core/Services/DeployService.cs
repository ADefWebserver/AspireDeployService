using System.ComponentModel;
using AspireDeployService.Core.Deploy;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Errors;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Interaction;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Prerequisites;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;

namespace AspireDeployService.Core.Services;

public sealed record DeployRequest(string ProfileName, string? RefOverride, bool DryRun, bool ClearCache, bool SkipConfirm = false);

public interface IDeployService
{
    /// <summary>Flows §7.3 and §7.4. Refusals are thrown as <see cref="DeployerException"/>; success returns code 0.</summary>
    Task<OperationResult> DeployAsync(DeployRequest request, IRunObserver observer, CancellationToken ct);
}

public sealed class DeployService(
    ProfileStore store,
    IProfileService profiles,
    IRunContextFactory runs,
    DiscoveryPipeline pipeline,
    PrerequisiteChecker checker,
    IUserInteraction interaction,
    TimeProvider time) : IDeployService
{
    /// <summary>Test seam: reads the parent environment (ASPIRE_CONTAINER_RUNTIME).</summary>
    internal Func<string, string?> ParentEnvironment { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Test seam: skips the az / container / node checks.</summary>
    internal bool SkipPrerequisites { get; init; }

    public async Task<OperationResult> DeployAsync(DeployRequest request, IRunObserver observer, CancellationToken ct)
    {
        var snapshot = await store.LoadAsync(ct);
        var options = snapshot.Options;
        if (!snapshot.Document.AspireDeploy.Profiles.TryGetValue(request.ProfileName, out var stored))
            throw new DeployerException(OutcomeCode.InvalidInput, $"Profile '{request.ProfileName}' does not exist.");
        var profile = stored.Clone();
        var errors = ProfileRules.Validate(profile);
        if (errors.Count > 0)
            throw new DeployerException(OutcomeCode.InvalidInput, "Fix the profile first:\n" + string.Join('\n', errors));

        var workKey = WorkKeys.For(options.WorkRoot, request.ProfileName, null);
        await using var workLock = WorkKeyLock.Acquire(workKey, request.ProfileName);
        await using var run = runs.Create(workKey, observer);

        var source = profile.ToSourceSpec(request.RefOverride);
        string? commit = null, version = null;
        var outcome = OutcomeCode.Unexpected;
        var category = ErrorCategory.Unknown;
        try
        {
            var result = await PrepareAsync(request, profile, source, options, workKey, run, ct);
            commit = result.Source.CommitSha;
            version = result.Scan.AppHost.AspireSdkVersion;

            var deployResult = await RunDeployAsync(request, profile, result, workKey, options, run, ct);
            outcome = deployResult.Code;
            category = deployResult.Category;
            return deployResult;
        }
        catch (DeployerException ex)
        {
            outcome = ex.Code;
            category = ex.Category;
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = OutcomeCode.DeployFailed;
            category = ErrorCategory.Canceled;
            throw;
        }
        finally
        {
            await DeployHistory.AppendAsync(workKey, new DeployHistory.Entry(
                time.GetUtcNow(), request.DryRun ? "deploy --dry-run" : "deploy", source.Display, commit, version,
                (int)outcome, category.ToString(), run.LogFile), CancellationToken.None);
        }
    }

    private async Task<PipelineResult> PrepareAsync(DeployRequest request, DeployProfile profile, SourceSpec source,
        AspireDeployOptions options, string workKey, RunContext run, CancellationToken ct)
    {
        var scanned = await pipeline.RunAsync(source, profile.Source.GitHubToken, profile.EnvironmentName, options, workKey, run,
            DiscoveryPipeline.Stop.AfterScan, assumeTrusted: true, ct);
        var appHost = scanned.Scan.AppHost;

        if (!appHost.HasAzureTarget)
            throw new DeployerException(OutcomeCode.DiscoveryFailed,
                "The AppHost declares no Azure compute environment. Add, for example, builder.AddAzureContainerAppEnvironment(\"env\"); "
                + "with the Aspire.Hosting.Azure.AppContainers package.");
        if (scanned.Scan.UsesSubmodulesOrLfs && source.Kind == SourceKind.GitHub)
            throw new DeployerException(OutcomeCode.DiscoveryFailed,
                "This repository uses submodules or Git LFS; clone it and use a local folder source.");

        if (!SkipPrerequisites)
        {
            var runtime = await checker.ContainerRuntimeAsync(run, profile.ContainerRuntime, required: true, ct);
            // No runtime configured and only Podman is running: tell aspire deploy to use it.
            if (runtime.DetectedRuntime is { } detected && string.IsNullOrWhiteSpace(profile.ContainerRuntime)
                && string.IsNullOrWhiteSpace(ParentEnvironment("ASPIRE_CONTAINER_RUNTIME")))
                profile.ContainerRuntime = detected;
            var failed = new List<PrerequisiteCheck>
            {
                await checker.AzureSignInAsync(run, profile.Destination.SubscriptionId, profile.Destination.TenantId, ct),
                runtime
            };
            if (appHost.Kind == AppHostKind.TypeScript)
                failed.Add(await checker.NodeAsync(run, required: true, ct));
            failed.RemoveAll(c => c.State != CheckState.Fail);
            if (failed.Count > 0)
                throw new DeployerException(OutcomeCode.MissingPrerequisite,
                    string.Join('\n', failed.Select(c => $"{c.Name}: {c.Detail} {c.Fix}")));
        }

        // Trust is asked here (not in the scan above) so a declined prompt runs nothing.
        var full = await pipeline.RunAsync(source with { AppHostPath = appHost.AppHostPath }, profile.Source.GitHubToken,
            profile.EnvironmentName, options, workKey, run, DiscoveryPipeline.Stop.AfterManifest, assumeTrusted: false, ct);

        var missing = full.Manifest!.Parameters
            .Where(p => p.NeedsValue && !profile.Parameters.ContainsKey(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (missing.Count > 0)
            throw new DeployerException(OutcomeCode.InvalidInput,
                $"These parameters have no value in profile '{request.ProfileName}': {string.Join(", ", missing)}. "
                + "Set them on the profile page, or tick \"AppHost supplies it\".");
        return full;
    }

    private async Task<OperationResult> RunDeployAsync(DeployRequest request, DeployProfile profile, PipelineResult prepared,
        string workKey, AspireDeployOptions options, RunContext run, CancellationToken ct)
    {
        var parameters = prepared.Manifest!.Parameters;
        var secretNames = profile.SecretParameters.Concat(parameters.Where(p => p.Secret).Select(p => p.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, value) in profile.Parameters)
        {
            if (secretNames.Contains(name))
                run.Redactor.AddSecret(value);
        }

        var d = profile.Destination;
        var version = prepared.Scan.AppHost.AspireSdkVersion;
        var message = $"Deploy {request.ProfileName} ({prepared.Source.Display}) to\n"
            + $"  tenant: {d.TenantId ?? "(the az login tenant)"}\n"
            + $"  subscription: {d.SubscriptionId}\n"
            + $"  resource group: {d.ResourceGroup} in {d.Location}\n"
            + $"  environment: {profile.EnvironmentName}";
        if (profile.LastDeployedAspireVersion is { } last && !string.Equals(last, version, StringComparison.OrdinalIgnoreCase))
        {
            var change = $"The AppHost's Aspire version changed from {last} to {version} since the last deploy.";
            message += "\n\n" + change;
            await run.Log.InfoAsync("Warning: " + change, ct);
        }
        if (request.DryRun)
            message += "\n\nDry run: only the deployment steps are listed.";

        if (!request.SkipConfirm)
        {
            var ok = await interaction.ConfirmAsync("Deploy to Azure?", message, request.DryRun ? "List steps" : "Deploy", ct);
            ct.ThrowIfCancellationRequested();
            if (!ok)
                throw new DeployerException(OutcomeCode.Declined, "Stopped: the deployment was not confirmed.");
        }

        var args = new List<string>
        {
            "deploy", "--apphost", prepared.Scan.AppHost.AppHostPath, "-e", profile.EnvironmentName,
            "-o", Path.Combine(workKey, "aspire-output"), "--non-interactive", "--nologo"
        };
        if (request.ClearCache)
            args.Add("--clear-cache");
        if (request.DryRun)
            args.Add("--list-steps");

        var cli = prepared.Cli!;
        var spec = new ProcessSpec(cli.Command, args, prepared.Source.RootFolder)
        {
            Timeout = TimeSpan.FromMinutes(options.DeployTimeoutMinutes),
            Environment = DeployEnvironment.Build(profile, parameters, cli.Folder, ParentEnvironment)
        };

        ProcessResult result;
        try
        {
            result = await run.Runner.RunAsync(spec, run.Log, ct);
        }
        catch (OperationCanceledException ex)
        {
            throw new DeployerException(OutcomeCode.DeployFailed,
                $"Deployment stopped. Resources may be partly created in {d.ResourceGroup}; deploy again to converge.", ErrorCategory.Canceled, ex);
        }
        catch (TimeoutException ex)
        {
            throw new DeployerException(OutcomeCode.DeployFailed,
                $"aspire deploy did not finish within {options.DeployTimeoutMinutes} minutes and was stopped. "
                + $"Resources may be partly created in {d.ResourceGroup}.", ErrorCategory.Timeout, ex);
        }
        catch (Win32Exception ex)
        {
            throw new DeployerException(OutcomeCode.MissingPrerequisite, "The Aspire CLI could not be started.", inner: ex);
        }

        if (!result.Succeeded)
        {
            var classified = ErrorClassifier.ClassifyText(result.OutputTail);
            throw new DeployerException(OutcomeCode.DeployFailed,
                $"aspire deploy failed (exit {result.ExitCode}). {classified?.Summary ?? "See the log for details."}",
                classified?.Category ?? ErrorCategory.Unknown);
        }

        if (!request.DryRun)
        {
            try
            {
                await profiles.RecordDeployedVersionAsync(request.ProfileName, version, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // D18: the deploy already succeeded; a settings write failure (read-only or locked file) is only a warning.
                await run.Log.InfoAsync("Warning: the deployed Aspire version could not be saved to the profile: " + ex.Message, CancellationToken.None);
            }
        }

        return OperationResult.Ok(request.DryRun
            ? "Dry run finished: the deployment steps are listed in the log."
            : $"Deployed {request.ProfileName} to resource group {d.ResourceGroup}. The endpoints are in the log above.", run.LogFile);
    }
}
