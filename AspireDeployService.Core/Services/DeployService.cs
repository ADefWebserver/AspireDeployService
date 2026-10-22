using System.ComponentModel;
using AspireDeployService.Core.Azure;
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
    IAzureResourceProvisioner provisioner,
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
            var failed = new List<PrerequisiteCheck>
            {
                await checker.AzureSignInAsync(run, profile.Destination.SubscriptionId, profile.Destination.TenantId, ct),
                await checker.ContainerRuntimeAsync(run, profile.ContainerRuntime, required: true, ct)
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
        var sourceRoot = prepared.Source.RootFolder;

        // Resource pickers plan §7.2: overlay, plan and preview run before the confirmation and write nothing to Azure.
        var overlayRunId = Guid.NewGuid().ToString("N");
        OverlayResult? overlay = null;
        IReadOnlyList<ProvisionStep> plan = [];
        foreach (var leftover in SiblingOverlay.CleanupLeftovers(sourceRoot))
            await run.Log.InfoAsync($"Removed a leftover temporary file from an earlier deploy: {leftover}", ct);
        try
        {
            if (d.NeedsOverlay)
            {
                if (!prepared.Scan.AppHost.DeclaredTargets.Contains(DeploymentTarget.AzureContainerApps))
                    throw new DeployerException(OutcomeCode.InvalidInput,
                        "The registry, environment and pull identity choices apply to Azure Container Apps only, and this AppHost does not declare "
                        + "AddAzureContainerAppEnvironment. Set them back to Let Aspire create.");
                overlay = AppHostOverlay.Prepare(OverlayRequestFor(profile, prepared, workKey, overlayRunId));
                await run.Log.InfoAsync("AppHost overlay prepared: " + string.Join(", ", overlay.Files), ct);
                plan = await provisioner.PlanAsync(profile, run, ct);
            }

            var compute = prepared.Manifest!.Resources.Where(ContainerAppPlanner.IsCompute).ToList();
            var preview = compute.Count == 0 || !prepared.Scan.AppHost.DeclaredTargets.Contains(DeploymentTarget.AzureContainerApps)
                ? []
                : await provisioner.PreviewAsync(profile, compute, plan, run, ct);
            var conflicts = preview.Where(r => r.Action == AppPlanAction.Conflict).ToList();
            if (conflicts.Count > 0)
            {
                throw new DeployerException(OutcomeCode.InvalidInput,
                    "These container apps cannot be deployed:\n" + string.Join('\n', conflicts.Select(c => $"  {c.AppName}: {c.Note}")));
            }

            var message = $"Deploy {request.ProfileName} ({prepared.Source.Display}) to\n"
                + $"  tenant: {d.TenantId ?? "(the az login tenant)"}\n"
                + $"  subscription: {d.SubscriptionId}\n"
                + $"  resource group: {d.ResourceGroup} in {d.Location}"
                + (plan.Any(s => s.Kind == AzureResourceKind.ResourceGroup) ? " (create)" : "") + "\n"
                + string.Concat(plan.Where(s => s.Kind != AzureResourceKind.ResourceGroup).Select(s => "  " + ConfirmLine(s) + "\n"))
                + (preview.Count > 0 ? "  container apps: " + string.Join(", ", preview.Select(ContainerAppPlanner.Describe)) + "\n" : "")
                + $"  environment: {profile.EnvironmentName}";
            if (plan.Any(s => s.Kind == AzureResourceKind.ContainerAppEnvironment && s.Action == ProvisionAction.Create))
                message += "\n\nCreating the environment can take 30 minutes or more.";
            await run.Log.InfoAsync(message, ct);
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

            var useOverlay = overlay;
            if (request.DryRun && overlay is not null && plan.Any(s => s.Action == ProvisionAction.Create))
            {
                var missing = plan.First(s => s.Action == ProvisionAction.Create).Name;
                await run.Log.InfoAsync($"Listing steps without the overlay because {missing} does not exist yet.", ct);
                useOverlay = null;
            }
            if (request.DryRun && plan.Count > 0)
                await run.Log.InfoAsync("Dry run: nothing is created in Azure. Planned: " + string.Join("; ", plan.Select(ConfirmLine)), ct);

            if (!request.DryRun && plan.Any(s => s.Action is ProvisionAction.Create or ProvisionAction.AlreadyThere))
            {
                try
                {
                    plan = await provisioner.EnsureAsync(request.ProfileName, profile, plan, run, ct);
                }
                catch (OperationCanceledException ex)
                {
                    throw new DeployerException(OutcomeCode.DeployFailed,
                        "Stopped while creating Azure resources. Azure may still be creating them; the next deploy checks their state.",
                        ErrorCategory.Canceled, ex);
                }
                catch (TimeoutException ex)
                {
                    throw new DeployerException(OutcomeCode.DeployFailed,
                        "Creating an Azure resource did not finish in time and was stopped. Azure may still be creating it; "
                        + "the next deploy checks its state.", ErrorCategory.Timeout, ex);
                }
            }

            return await RunAspireDeployAsync(request, profile, prepared, workKey, options, run, useOverlay, overlayRunId, version, ct);
        }
        finally
        {
            await DeleteSiblingAsync(overlay, run);
        }
    }

    private async Task<OperationResult> RunAspireDeployAsync(DeployRequest request, DeployProfile profile, PipelineResult prepared,
        string workKey, AspireDeployOptions options, RunContext run, OverlayResult? overlay, string overlayRunId, string version, CancellationToken ct)
    {
        var d = profile.Destination;
        var parameters = prepared.Manifest!.Parameters;
        var args = new List<string>
        {
            "deploy", "--apphost", overlay?.AppHostPath ?? prepared.Scan.AppHost.AppHostPath, "-e", profile.EnvironmentName,
            "-o", Path.Combine(workKey, "aspire-output"), "--non-interactive", "--nologo"
        };
        if (request.ClearCache)
            args.Add("--clear-cache");
        if (request.DryRun)
            args.Add("--list-steps");

        var environment = DeployEnvironment.Build(profile, parameters, prepared.Cli!.Folder, ParentEnvironment);
        if (overlay is not null)
        {
            foreach (var (key, value) in overlay.Environment)
                environment[key] = value;
        }

        // Aspire's deploy-state cache is per AppHost and wins over environment variables (found in the end-to-end test):
        // a cache written for another profile's destination, or old overlay names, would be reused silently.
        var appHostFull = Path.Combine(prepared.Source.RootFolder, overlay?.AppHostPath ?? prepared.Scan.AppHost.AppHostPath);
        if (AspireDeployState.ResetForOtherDestination(appHostFull, profile.EnvironmentName, d.SubscriptionId!, d.ResourceGroup!, d.Location!)
            is { } other)
            await run.Log.InfoAsync($"Aspire's deploy cache for this AppHost was written for {other}; its Azure entries were cleared "
                + $"so this deploy goes to {d.ResourceGroup}.", ct);
        if (overlay is not null)
        {
            var cleared = AspireDeployState.RemoveOverlayParameters(appHostFull, profile.EnvironmentName);
            if (cleared.Count > 0)
                await run.Log.InfoAsync("Cleared Aspire's cached values for " + string.Join(", ", cleared) + ".", ct);
        }

        var cli = prepared.Cli!;
        var spec = new ProcessSpec(cli.Command, args, prepared.Source.RootFolder)
        {
            Timeout = TimeSpan.FromMinutes(options.DeployTimeoutMinutes),
            Environment = environment
        };

        ProcessResult result;
        try
        {
            await using var appHostLock = await AcquireAppHostLockAsync(options.WorkRoot, prepared, ct);
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

        // R9: an ignored overlay would let Aspire create a new environment and registry silently.
        if (overlay is not null && !request.DryRun && !File.Exists(AppHostOverlay.MarkerFile(workKey, overlayRunId)))
            throw new DeployerException(OutcomeCode.DeployFailed,
                $"The AppHost overlay was not applied, so Aspire may have created a new environment or registry in {d.ResourceGroup}. See Troubleshooting.");

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

    private static OverlayRequest OverlayRequestFor(DeployProfile profile, PipelineResult prepared, string workKey, string runId)
    {
        var d = profile.Destination;
        string GroupOf(ResourceChoice c) =>
            c.EffectiveMode == ResourceMode.Existing && c.ResourceGroup is { Length: > 0 } g ? g : d.ResourceGroup!;
        var env = d.EnvironmentMode == ResourceMode.AspireDefault ? null : d.ContainerAppEnvironment;
        var reg = d.RegistryMode == ResourceMode.AspireDefault ? null : d.ContainerRegistry;
        var id = d.IdentityMode == ResourceMode.Existing ? d.AcrPullIdentity : null;
        return new OverlayRequest(prepared.Source.RootFolder, prepared.Scan.AppHost, workKey, runId,
            prepared.Manifest!.Resources.Select(r => r.Name).ToList(),
            d.ContainerAppEnvironment?.AppHostResource,
            env?.Name, env is null ? null : GroupOf(env),
            reg?.Name, reg is null ? null : GroupOf(reg),
            id?.Name, id?.ResourceGroup);
    }

    private static string ConfirmLine(ProvisionStep s)
    {
        var label = s.Kind switch
        {
            AzureResourceKind.ContainerRegistry => "registry",
            AzureResourceKind.ContainerAppEnvironment => "environment",
            AzureResourceKind.ManagedIdentity => "pull identity",
            _ => "resource group"
        };
        var action = s.Action switch
        {
            ProvisionAction.Create => "create" + (s.Detail is { } detail ? ", " + detail : ""),
            ProvisionAction.AlreadyThere => "already there",
            _ => "existing" + (s.Kind == AzureResourceKind.ManagedIdentity && s.Detail is { } note ? " " + note : "")
        };
        return $"{label}: {s.Name} in {s.ResourceGroup} ({action})";
    }

    /// <summary>
    /// <c>&lt;WorkRoot&gt;/locks/apphost-&lt;12 hex&gt;.lock</c>: two profiles over one local folder must not build the same
    /// obj/bin at once, one with the overlay and one without (resource pickers plan §6.4).
    /// </summary>
    internal static async Task<FileLock> AcquireAppHostLockAsync(string workRoot, PipelineResult prepared, CancellationToken ct)
    {
        var path = DiscoveryPipeline.AppHostLockPath(workRoot, Path.Combine(prepared.Source.RootFolder, prepared.Scan.AppHost.AppHostPath));
        try
        {
            return await FileLock.AcquireAsync(path, DiscoveryPipeline.AppHostLockTimeout, ct);
        }
        catch (IOException ex)
        {
            throw new DeployerException(OutcomeCode.InvalidInput, "Another run is building this AppHost. Wait for it to finish.", inner: ex);
        }
    }

    /// <summary>D3: the sibling copy is removed after every deploy, whatever the outcome.</summary>
    private static async Task DeleteSiblingAsync(OverlayResult? overlay, RunContext run)
    {
        if (overlay?.SiblingFile is not { } sibling)
            return;
        var folder = Path.GetDirectoryName(sibling)!;
        if (!Directory.Exists(folder))
            return;
        string line;
        try
        {
            line = SiblingOverlay.Remove(sibling)
                ? $"Removed the temporary folder {folder}."
                : $"Warning: {folder} no longer holds Aspire Deploy's marker, so it was left in place.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            line = $"Warning: could not remove the temporary file {sibling} ({ex.Message}). The next run or app start removes it.";
        }
        await run.Log.InfoAsync(line, CancellationToken.None);
    }
}

