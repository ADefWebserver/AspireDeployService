using System.Text.Json;
using AspireDeployService.Core.Deploy;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Core.Azure;

public enum ProvisionAction { UseExisting, Create, AlreadyThere }

/// <param name="ResourceId">The Azure resource ID when it is known (existing resources, and after a create).</param>
/// <param name="WorkspaceId">For a new environment with existing-workspace logs: the workspace's resource ID (D14).</param>
public sealed record ProvisionStep(AzureResourceKind Kind, string Name, string ResourceGroup, ProvisionAction Action, string? Detail,
    string? ResourceId = null, string? WorkspaceId = null);

public interface IAzureResourceProvisioner
{
    /// <summary>Read-only: decides what <see cref="EnsureAsync"/> would do. Empty when every section is Let Aspire create.</summary>
    Task<IReadOnlyList<ProvisionStep>> PlanAsync(DeployProfile profile, RunContext run, CancellationToken ct);

    /// <summary>
    /// Runs the Create steps of <paramref name="plan"/> in order (resource group, registry, environment), then
    /// invalidates the cached lists. After each create (or a New name found already there) the profile's section is
    /// switched to Existing (D15). Returns the steps with resource IDs filled in.
    /// </summary>
    Task<IReadOnlyList<ProvisionStep>> EnsureAsync(string profileName, DeployProfile profile, IReadOnlyList<ProvisionStep> plan,
        RunContext run, CancellationToken ct);

    /// <summary>Works out the environment ID, lists the apps and calls <see cref="ContainerAppPlanner.Plan"/>.</summary>
    Task<IReadOnlyList<AppPlanRow>> PreviewAsync(DeployProfile profile, IReadOnlyList<DiscoveredResource> compute,
        IReadOnlyList<ProvisionStep>? plan, RunContext run, CancellationToken ct);

    /// <summary>The same preview for the new-profile wizard, in a run of its own (like the editor's list calls).</summary>
    Task<IReadOnlyList<AppPlanRow>> PreviewAsync(DeployProfile profile, IReadOnlyList<DiscoveredResource> compute,
        IRunObserver observer, CancellationToken ct);
}

/// <summary>Resource pickers plan §6.5: the <c>az</c> reads and creates that run inside the deploy run, before <c>aspire deploy</c>.</summary>
public sealed class AzureResourceProvisioner(ProfileStore store, IAzureAccountService account, IRunContextFactory runs) : IAzureResourceProvisioner
{
    public const string Tag = "created-by=AspireDeployService";
    internal static TimeSpan ShowTimeout { get; set; } = TimeSpan.FromMinutes(2);
    internal static TimeSpan RegistryTimeout { get; set; } = TimeSpan.FromMinutes(10);
    internal static TimeSpan EnvironmentTimeout { get; set; } = TimeSpan.FromMinutes(45);

    public async Task<IReadOnlyList<ProvisionStep>> PlanAsync(DeployProfile profile, RunContext run, CancellationToken ct)
    {
        var d = profile.Destination;
        var anyNew = d.RegistryMode == ResourceMode.New || d.EnvironmentMode == ResourceMode.New;
        // aspire deploy (13.6.1) creates a missing group even with Azure:AllowResourceGroupCreation=false (wizard plan T0),
        // so the app checks it itself whenever creation is off, overlay or not.
        if (!anyNew && d.AllowResourceGroupCreation != false && !d.NeedsOverlay)
            return [];
        var sub = d.SubscriptionId!;
        var rg = d.ResourceGroup!;
        var steps = new List<ProvisionStep>();

        if (anyNew || d.AllowResourceGroupCreation == false)
        {
            var exists = await RunAsync(["group", "exists", "--name", rg, "--subscription", sub, "--output", "json"], run, ShowTimeout, ct);
            Fail(exists, "resource group", rg);
            if (!exists.StdOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                if (d.AllowResourceGroupCreation == false)
                    throw new DeployerException(OutcomeCode.InvalidInput,
                        $"Resource group {rg} does not exist and the profile does not allow creating it.");
                steps.Add(new(AzureResourceKind.ResourceGroup, rg, rg, ProvisionAction.Create, d.Location));
            }
        }
        if (!d.NeedsOverlay)
            return steps;

        ProvisionStep? registry = null;
        if (d.ContainerRegistry is { } r && r.EffectiveMode != ResourceMode.AspireDefault)
        {
            var group = r.EffectiveMode == ResourceMode.Existing && r.ResourceGroup is { Length: > 0 } g ? g : rg;
            var show = await RunAsync(["acr", "show", "--name", r.Name!, "--resource-group", group, "--subscription", sub, "--output", "json"],
                run, ShowTimeout, ct);
            if (show.Succeeded)
            {
                using var doc = JsonDocument.Parse(show.StdOut);
                CheckState(AzureResourceKind.ContainerRegistry, r.Name!, Text(doc.RootElement, "provisioningState"));
                registry = new(AzureResourceKind.ContainerRegistry, r.Name!, group,
                    r.EffectiveMode == ResourceMode.Existing ? ProvisionAction.UseExisting : ProvisionAction.AlreadyThere,
                    Text(doc.RootElement, "loginServer"), Text(doc.RootElement, "id"));
            }
            else if (show.Kind is AzErrorKind.NotFound or AzErrorKind.ResourceGroupNotFound)
            {
                if (r.EffectiveMode == ResourceMode.Existing)
                    throw NotFound(AzureResourceKind.ContainerRegistry, r.Name!, group);
                var check = await RunAsync(["acr", "check-name", "--name", r.Name!, "--subscription", sub, "--output", "json"], run, ShowTimeout, ct);
                Fail(check, "registry name", r.Name!);
                var availability = AzureAccountService.ParseNameAvailability(check.StdOut);
                if (!availability.Available)
                    throw new DeployerException(OutcomeCode.InvalidInput, $"The registry name {r.Name} is taken in Azure; choose another.");
                registry = new(AzureResourceKind.ContainerRegistry, r.Name!, rg, ProvisionAction.Create, "sku " + r.EffectiveSku);
            }
            else
                Fail(show, "container registry", r.Name!);
            steps.Add(registry!);
        }

        if (d.ContainerAppEnvironment is { } e && e.EffectiveMode != ResourceMode.AspireDefault)
        {
            var group = e.EffectiveMode == ResourceMode.Existing && e.ResourceGroup is { Length: > 0 } g ? g : rg;
            var show = await RunAsync(["containerapp", "env", "show", "--name", e.Name!, "--resource-group", group, "--subscription", sub, "--output", "json"],
                run, ShowTimeout, ct);
            if (show.Succeeded)
            {
                using var doc = JsonDocument.Parse(show.StdOut);
                var root = doc.RootElement;
                var state = root.TryGetProperty("properties", out var props) ? Text(props, "provisioningState") : "";
                CheckState(AzureResourceKind.ContainerAppEnvironment, e.Name!, state);
                var location = Text(root, "location");
                if (!SameRegion(location, d.Location))
                    throw new DeployerException(OutcomeCode.InvalidInput,
                        $"The environment {e.Name} is in {location}, but the profile deploys to {d.Location}. Container apps must be in their environment's region.");
                steps.Add(new(AzureResourceKind.ContainerAppEnvironment, e.Name!, group,
                    e.EffectiveMode == ResourceMode.Existing ? ProvisionAction.UseExisting : ProvisionAction.AlreadyThere, null, Text(root, "id")));
            }
            else if (show.Kind is AzErrorKind.NotFound or AzErrorKind.ResourceGroupNotFound)
            {
                if (e.EffectiveMode == ResourceMode.Existing)
                    throw NotFound(AzureResourceKind.ContainerAppEnvironment, e.Name!, group);
                string? workspaceId = null;
                var dest = e.EffectiveLogsDestination;
                if (dest == ResourceChoice.LogsExistingWorkspace)
                {
                    var ws = e.LogsWorkspace!;
                    var wsShow = await RunAsync(["monitor", "log-analytics", "workspace", "show", "--workspace-name", ws.Name!,
                        "--resource-group", ws.ResourceGroup!, "--subscription", sub, "--output", "json"], run, ShowTimeout, ct);
                    if (wsShow.Kind is AzErrorKind.NotFound or AzErrorKind.ResourceGroupNotFound)
                        throw new DeployerException(OutcomeCode.InvalidInput, $"Log Analytics workspace {ws.Name} was not found in {ws.ResourceGroup}.");
                    Fail(wsShow, "Log Analytics workspace", ws.Name!);
                    using var wsDoc = JsonDocument.Parse(wsShow.StdOut);
                    workspaceId = Text(wsDoc.RootElement, "id");
                }
                steps.Add(new(AzureResourceKind.ContainerAppEnvironment, e.Name!, rg, ProvisionAction.Create,
                    dest == ResourceChoice.LogsExistingWorkspace ? $"logs: {e.LogsWorkspace!.Name}" : "logs: " + dest, WorkspaceId: workspaceId));
            }
            else
                Fail(show, "Container Apps environment", e.Name!);
        }

        if (d.AcrPullIdentity is { } i && i.EffectiveMode == ResourceMode.Existing)
        {
            var show = await RunAsync(["identity", "show", "--name", i.Name!, "--resource-group", i.ResourceGroup!, "--subscription", sub, "--output", "json"],
                run, ShowTimeout, ct);
            if (show.Kind is AzErrorKind.NotFound or AzErrorKind.ResourceGroupNotFound)
                throw NotFound(AzureResourceKind.ManagedIdentity, i.Name!, i.ResourceGroup!);
            Fail(show, "managed identity", i.Name!);
            using var doc = JsonDocument.Parse(show.StdOut);
            var principal = Text(doc.RootElement, "principalId");
            var id = Text(doc.RootElement, "id");
            string? detail = null;
            if (registry?.ResourceId is { Length: > 0 } scope)
            {
                var roles = await RunAsync(["role", "assignment", "list", "--assignee", principal, "--scope", scope, "--role", "AcrPull",
                    "--subscription", sub, "--output", "json"], run, ShowTimeout, ct);
                if (roles.Kind == AzErrorKind.Unauthorized)
                    detail = "(could not check AcrPull)";
                else
                {
                    Fail(roles, "role assignments of", i.Name!);
                    using var list = JsonDocument.Parse(roles.StdOut);
                    if (list.RootElement.ValueKind != JsonValueKind.Array || list.RootElement.GetArrayLength() == 0)
                        throw new DeployerException(OutcomeCode.InvalidInput,
                            $"Identity {i.Name} cannot pull from {registry.Name}. Ask an owner to run: az role assignment create --assignee {principal} --role AcrPull --scope {scope}");
                }
            }
            steps.Add(new(AzureResourceKind.ManagedIdentity, i.Name!, i.ResourceGroup!, ProvisionAction.UseExisting, detail, id));
        }
        return steps;
    }

    public async Task<IReadOnlyList<ProvisionStep>> EnsureAsync(string profileName, DeployProfile profile, IReadOnlyList<ProvisionStep> plan,
        RunContext run, CancellationToken ct)
    {
        var d = profile.Destination;
        var sub = d.SubscriptionId!;
        var location = d.Location!;
        var done = new List<ProvisionStep>();
        var created = new List<string>();
        try
        {
            foreach (var step in plan)
            {
                if (step.Action != ProvisionAction.Create)
                {
                    done.Add(step);
                    if (step.Action == ProvisionAction.AlreadyThere)
                        await SwitchToExistingAsync(profileName, step, run, ct);
                    continue;
                }

                var display = AzureNameRules.Display(step.Kind);
                switch (step.Kind)
                {
                    case AzureResourceKind.ResourceGroup:
                        await run.Log.InfoAsync($"Creating resource group {step.Name} in {location}.", ct);
                        Created(await RunAsync(["group", "create", "--name", step.Name, "--location", location, "--subscription", sub,
                            "--tags", Tag, "--output", "json"], run, ShowTimeout, ct), step, created);
                        done.Add(step);
                        break;

                    case AzureResourceKind.ContainerRegistry:
                        await run.Log.InfoAsync($"Creating {display.ToLowerInvariant()} {step.Name} in {step.ResourceGroup}. This can take a few minutes.", ct);
                        var acr = await RunAsync(["acr", "create", "--name", step.Name, "--resource-group", step.ResourceGroup, "--location", location,
                            "--sku", d.ContainerRegistry!.EffectiveSku, "--subscription", sub, "--tags", Tag, "--output", "json"], run, RegistryTimeout, ct);
                        Created(acr, step, created);
                        done.Add(step with { ResourceId = IdOf(acr.StdOut) });
                        await SwitchToExistingAsync(profileName, step, run, ct);
                        break;

                    case AzureResourceKind.ContainerAppEnvironment:
                        var choice = d.ContainerAppEnvironment!;
                        var dest = choice.EffectiveLogsDestination;
                        await run.Log.InfoAsync($"Creating {display} {step.Name} in {step.ResourceGroup}. This can take 30 minutes or more.", ct);
                        var env = await RunAsync(["containerapp", "env", "create", "--name", step.Name, "--resource-group", step.ResourceGroup,
                            "--location", location, "--logs-destination", dest == ResourceChoice.LogsExistingWorkspace ? "azure-monitor" : dest,
                            "--subscription", sub, "--tags", Tag, "--output", "json"], run, EnvironmentTimeout, ct);
                        Created(env, step, created);
                        var envId = IdOf(env.StdOut)
                            ?? $"/subscriptions/{sub}/resourceGroups/{step.ResourceGroup}/providers/Microsoft.App/managedEnvironments/{step.Name}";
                        done.Add(step with { ResourceId = envId });
                        // D15: switch before the logs routing, so a retry after a routing failure does not re-create the environment.
                        await SwitchToExistingAsync(profileName, step, run, ct);
                        if (dest == ResourceChoice.LogsExistingWorkspace)
                        {
                            var routing = await RunAsync(["monitor", "diagnostic-settings", "create", "--name", "ads-logs", "--resource", envId,
                                "--workspace", step.WorkspaceId!, "--logs", """[{"categoryGroup":"allLogs","enabled":true}]""", "--output", "json"],
                                run, ShowTimeout, ct);
                            if (!routing.Succeeded)
                                throw new DeployerException(OutcomeCode.DeployFailed,
                                    $"The environment was created, but its logs could not be sent to {choice.LogsWorkspace!.Name}: {routing.ErrorLine}. "
                                    + "Add a diagnostic setting in the portal.");
                            await run.Log.InfoAsync($"Logs of {step.Name} are sent to Log Analytics workspace {choice.LogsWorkspace!.Name}.", ct);
                        }
                        break;
                }
            }
            return done;
        }
        catch (DeployerException ex) when (created.Count > 0 && ex.Code == OutcomeCode.DeployFailed && !ex.Message.Contains("Already created", StringComparison.Ordinal))
        {
            throw new DeployerException(ex.Code, ex.Message + " Already created: " + string.Join(", ", created) + ". It is safe to deploy again.",
                ex.Category, ex);
        }
        finally
        {
            if (plan.Any(s => s.Action == ProvisionAction.Create))
                account.Invalidate(sub);
        }
    }

    public async Task<IReadOnlyList<AppPlanRow>> PreviewAsync(DeployProfile profile, IReadOnlyList<DiscoveredResource> compute,
        IReadOnlyList<ProvisionStep>? plan, RunContext run, CancellationToken ct)
    {
        var d = profile.Destination;
        var sub = d.SubscriptionId!;
        var rg = d.ResourceGroup!;

        IReadOnlyList<AzureContainerApp>? inGroup = await ListAppsAsync(sub, rg, run, ct);

        string? environmentId = null;
        switch (d.EnvironmentMode)
        {
            case ResourceMode.Existing:
                environmentId = plan?.FirstOrDefault(s => s.Kind == AzureResourceKind.ContainerAppEnvironment)?.ResourceId;
                // The editor switches to Use existing before an environment is picked; there is nothing to look up yet.
                if (environmentId is null && d.ContainerAppEnvironment is { Name: { Length: > 0 } } e)
                {
                    var show = await RunAsync(["containerapp", "env", "show", "--name", e.Name, "--resource-group",
                        e.ResourceGroup is { Length: > 0 } g ? g : rg, "--subscription", sub, "--output", "json"], run, ShowTimeout, ct);
                    environmentId = show.Succeeded ? IdOf(show.StdOut) : null;
                }
                break;
            case ResourceMode.New when d.ContainerAppEnvironment is { Name: { Length: > 0 } newName }:
                environmentId = $"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.App/managedEnvironments/{newName}";
                break;
            case ResourceMode.New:
                break;
            default:
                var ids = inGroup?.Select(a => a.EnvironmentId).Where(i => i is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                environmentId = ids is { Count: 1 } ? ids[0] : null;
                break;
        }

        IReadOnlyList<AzureContainerApp>? inEnvironment = [];
        if (environmentId is not null)
        {
            var all = await ListAppsAsync(sub, null, run, ct);
            inEnvironment = all?.Where(a => string.Equals(a.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return ContainerAppPlanner.Plan(compute, new AppPlanInput(rg, environmentId, inGroup, inEnvironment));
    }

    public async Task<IReadOnlyList<AppPlanRow>> PreviewAsync(DeployProfile profile, IReadOnlyList<DiscoveredResource> compute,
        IRunObserver observer, CancellationToken ct)
    {
        var options = (await store.LoadAsync(ct)).Options;
        await using var run = runs.Create(Infrastructure.WorkKeys.For(options.WorkRoot, null, null), observer);
        return await PreviewAsync(profile, compute, null, run, ct);
    }

    /// <summary>Always fresh (the deploy may run minutes after the editor's cached list). Null when the apps could not be read.</summary>
    private static async Task<IReadOnlyList<AzureContainerApp>?> ListAppsAsync(string sub, string? rg, RunContext run, CancellationToken ct)
    {
        List<string> args = ["containerapp", "list", "--subscription", sub];
        if (rg is not null)
            args.AddRange(["--resource-group", rg]);
        args.AddRange(["--output", "json"]);
        var result = await RunAsync(args, run, ShowTimeout, ct);
        if (result.Kind == AzErrorKind.ResourceGroupNotFound)
            return [];
        if (!result.Succeeded)
            return null;
        try
        {
            return AzureAccountService.ParseContainerApps(result.StdOut);
        }
        catch (DeployerException)
        {
            return null;
        }
    }

    /// <summary>D15: the profile now names what is really in Azure. A failed write is only a warning.</summary>
    private async Task SwitchToExistingAsync(string profileName, ProvisionStep step, RunContext run, CancellationToken ct)
    {
        if (step.Kind is not (AzureResourceKind.ContainerRegistry or AzureResourceKind.ContainerAppEnvironment))
            return;
        var display = AzureNameRules.Display(step.Kind);
        try
        {
            var switched = await store.UpdateAsync(doc =>
            {
                if (!doc.AspireDeploy.Profiles.TryGetValue(profileName, out var p))
                    return false;
                var choice = step.Kind == AzureResourceKind.ContainerRegistry
                    ? p.Destination.ContainerRegistry ??= new()
                    : p.Destination.ContainerAppEnvironment ??= new();
                if (choice.EffectiveMode != ResourceMode.New || !string.Equals(choice.Name, step.Name, StringComparison.OrdinalIgnoreCase))
                    return false;
                choice.Mode = ResourceMode.Existing;
                choice.ResourceGroup = step.ResourceGroup;
                choice.Sku = null;
                choice.LogsDestination = null;
                choice.LogsWorkspace = null;
                return true;
            }, ct);
            if (switched)
                await run.Log.InfoAsync($"Profile {profileName}: {display.ToLowerInvariant()} switched to Use existing ({step.Name}).", ct);
        }
        catch (Exception ex) when (ex is DeployerException or IOException or UnauthorizedAccessException)
        {
            await run.Log.InfoAsync($"Warning: The profile could not be updated; set {display.ToLowerInvariant()} to Use existing {step.Name} yourself. ({ex.Message})", ct);
        }
    }

    private static void Created(AzResult result, ProvisionStep step, List<string> created)
    {
        var display = AzureNameRules.Display(step.Kind);
        if (result.Succeeded)
        {
            created.Add($"{display.ToLowerInvariant()} {step.Name}");
            return;
        }
        throw result.Kind switch
        {
            AzErrorKind.Conflict => new DeployerException(OutcomeCode.DeployFailed, $"{display} {step.Name} was created by another run; deploy again."),
            AzErrorKind.Unauthorized => new DeployerException(OutcomeCode.InvalidInput,
                $"You are not allowed to create {display.ToLowerInvariant()} {step.Name} in {step.ResourceGroup}: {result.ErrorLine}. "
                + "Creating needs Contributor on the resource group (or the subscription for a new group)."),
            AzErrorKind.ProviderNotRegistered => new DeployerException(OutcomeCode.InvalidInput, $"{result.ErrorLine}\n{AzCli.ProviderFix}"),
            _ => new DeployerException(OutcomeCode.DeployFailed, $"Could not create {display.ToLowerInvariant()} {step.Name}: {result.ErrorLine}")
        };
    }

    /// <summary>Throws for a failed read that is not "not found" (handled by the caller).</summary>
    private static void Fail(AzResult result, string what, string name)
    {
        if (result.Succeeded)
            return;
        throw result.Kind switch
        {
            AzErrorKind.NotSignedIn => new DeployerException(OutcomeCode.MissingPrerequisite, "The Azure CLI is not signed in. Run: az login"),
            AzErrorKind.Unauthorized => new DeployerException(OutcomeCode.InvalidInput,
                $"You are not allowed to read {what} {name}: {result.ErrorLine}. Reading needs Reader on the subscription."),
            AzErrorKind.ProviderNotRegistered => new DeployerException(OutcomeCode.InvalidInput, $"{result.ErrorLine}\n{AzCli.ProviderFix}"),
            _ => new DeployerException(OutcomeCode.DeployFailed, $"Could not read {what} {name}: {result.ErrorLine}")
        };
    }

    private static DeployerException NotFound(AzureResourceKind kind, string name, string group) =>
        new(OutcomeCode.InvalidInput, $"{AzureNameRules.Display(kind)} {name} was not found in {group}. Choose Create new to create it again, or pick another.");

    private static void CheckState(AzureResourceKind kind, string name, string state)
    {
        var display = AzureNameRules.Display(kind);
        switch (state)
        {
            case "" or "Succeeded":
                return;
            case "InProgress" or "Creating" or "Updating" or "Waiting" or "ScheduledForDelete":
                throw new DeployerException(OutcomeCode.InvalidInput, $"{display} {name} is still being created in Azure. Try again in a few minutes.");
            default:
                throw new DeployerException(OutcomeCode.InvalidInput,
                    $"{display} {name} exists but its creation failed. Delete it in the Azure portal or choose another name.");
        }
    }

    private static bool SameRegion(string a, string? b) =>
        string.Equals(Squash(a), Squash(b ?? ""), StringComparison.Ordinal);

    private static string Squash(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string? IdOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Text(doc.RootElement, "id") is { Length: > 0 } id ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static Task<AzResult> RunAsync(IReadOnlyList<string> args, RunContext run, TimeSpan timeout, CancellationToken ct) =>
        AzCli.RunAsync(args, run, timeout, ct);
}
