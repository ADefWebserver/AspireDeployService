using System.Text.Json;
using AspireDeployService.Core.Azure;
using AspireDeployService.Core.Deploy;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests.Azure;

/// <summary>Resource pickers plan §6.5 and §6.6: the az reads and creates, with FakeProcessRunner answering az.</summary>
public class AzureResourceProvisionerTests : IDisposable
{
    private const string Sub = "00000000-0000-0000-0000-000000000001";
    private const string SharedEnvId = $"/subscriptions/{Sub}/resourceGroups/rg-shared/providers/Microsoft.App/managedEnvironments/cae-shared";
    private const string RegistryId = $"/subscriptions/{Sub}/resourceGroups/rg-shared/providers/Microsoft.ContainerRegistry/registries/crshared01";
    private const string WorkspaceId = $"/subscriptions/{Sub}/resourceGroups/rg-logs/providers/Microsoft.OperationalInsights/workspaces/ws-shared";

    private readonly Harness _h = new();
    private readonly List<(string Prefix, ProcessResult Result)> _az = [];
    private readonly SpyAccount _account = new();
    private readonly AzureResourceProvisioner _provisioner;
    private readonly RunContext _run;

    public AzureResourceProvisionerTests()
    {
        _h.OnAz = spec =>
        {
            var args = string.Join(' ', spec.Arguments);
            return _az.FirstOrDefault(r => args.StartsWith(r.Prefix, StringComparison.Ordinal)).Result;
        };
        _provisioner = new AzureResourceProvisioner(_h.Store, _account, _h.Runs);
        _run = _h.Runs.Create(_h.Dir.Combine("w", "shop"), _h.Observer);
    }

    public void Dispose()
    {
        _run.DisposeAsync().AsTask().Wait();
        _h.Dispose();
    }

    private void On(string prefix, ProcessResult result) => _az.Add((prefix, result));
    private void On(string prefix, string json) => On(prefix, FakeProcessRunner.Ok(json));
    private static ProcessResult NotFound => FakeProcessRunner.Fail(3, "ERROR: (ResourceNotFound) The Resource 'x' under resource group 'g' was not found.");
    private static ProcessResult GroupNotFound => FakeProcessRunner.Fail(3, "ERROR: (ResourceGroupNotFound) Resource group 'rg-new' could not be found.");

    private static string Fixture(string name) => File.ReadAllText(AppHostFixtures.Fixture("az", name));

    private List<string> Calls => _h.AzSpecs.Select(s => string.Join(' ', s.Arguments)).ToList();

    private static string Env(string name, string rg, string location = "East US 2", string state = "Succeeded") =>
        JsonSerializer.Serialize(new
        {
            id = $"/subscriptions/{Sub}/resourceGroups/{rg}/providers/Microsoft.App/managedEnvironments/{name}",
            name, location, resourceGroup = rg, properties = new { provisioningState = state }
        });

    private static string Acr(string name, string rg, string state = "Succeeded") =>
        JsonSerializer.Serialize(new
        {
            id = $"/subscriptions/{Sub}/resourceGroups/{rg}/providers/Microsoft.ContainerRegistry/registries/{name}",
            name, location = "eastus2", loginServer = name + ".azurecr.io", provisioningState = state
        });

    private Task<DeployProfile> ProfileAsync(Action<ProfileDestination> change) =>
        _h.AddProfileAsync(change: p => change(p.Destination));

    private static ResourceChoice Existing(string name, string rg = "rg-shared") => new() { Mode = ResourceMode.Existing, Name = name, ResourceGroup = rg };

    private void ExistingSharedResources()
    {
        On("acr show --name crshared01 --resource-group rg-shared", Fixture("acr-show.json"));
        On("containerapp env show --name cae-shared --resource-group rg-shared", Fixture("containerapp-env-show.json"));
    }

    // --- PlanAsync ---

    [Fact]
    public async Task All_AspireDefault_plans_nothing_and_runs_no_az()
    {
        var p = await ProfileAsync(_ => { });
        Assert.Empty(await _provisioner.PlanAsync(p, _run, CancellationToken.None));
        Assert.Empty(_h.AzSpecs);
    }

    [Fact]
    public async Task Existing_registry_and_environment_are_used_with_their_ids()
    {
        ExistingSharedResources();
        var p = await ProfileAsync(d => { d.ContainerRegistry = Existing("crshared01"); d.ContainerAppEnvironment = Existing("cae-shared"); });

        var plan = await _provisioner.PlanAsync(p, _run, CancellationToken.None);

        Assert.Equal([AzureResourceKind.ContainerRegistry, AzureResourceKind.ContainerAppEnvironment], plan.Select(s => s.Kind));
        Assert.All(plan, s => Assert.Equal(ProvisionAction.UseExisting, s.Action));
        Assert.Equal(RegistryId, plan[0].ResourceId);
        Assert.Equal(SharedEnvId, plan[1].ResourceId);
        Assert.DoesNotContain(Calls, c => c.StartsWith("group exists", StringComparison.Ordinal)); // no New resource
        Assert.All(_h.AzSpecs, s => Assert.False(s.LogStdOut));
    }

    [Fact]
    public async Task Existing_not_found_is_refused()
    {
        On("acr show", NotFound);
        var p = await ProfileAsync(d => d.ContainerRegistry = Existing("crmissing01"));
        var ex = await Assert.ThrowsAsync<DeployerException>(() => _provisioner.PlanAsync(p, _run, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.StartsWith("Container registry crmissing01 was not found in rg-shared.", ex.Message);
        Assert.Contains("Choose Create new", ex.Message);
    }

    [Fact]
    public async Task Environment_in_another_region_is_refused()
    {
        On("containerapp env show", Env("cae-west", "rg-shared", "West US"));
        var p = await ProfileAsync(d => d.ContainerAppEnvironment = Existing("cae-west"));
        var ex = await Assert.ThrowsAsync<DeployerException>(() => _provisioner.PlanAsync(p, _run, CancellationToken.None));
        Assert.Equal("The environment cae-west is in West US, but the profile deploys to eastus2. Container apps must be in their environment's region.", ex.Message);
    }

    [Theory]
    [InlineData("InProgress", "is still being created in Azure. Try again in a few minutes.")]
    [InlineData("Waiting", "is still being created in Azure.")]
    [InlineData("Failed", "exists but its creation failed. Delete it in the Azure portal or choose another name.")]
    [InlineData("Canceled", "exists but its creation failed.")]
    public async Task Unfinished_or_failed_resources_are_refused(string state, string text)
    {
        On("containerapp env show", Env("cae-shared", "rg-shared", state: state));
        var p = await ProfileAsync(d => d.ContainerAppEnvironment = Existing("cae-shared"));
        var ex = await Assert.ThrowsAsync<DeployerException>(() => _provisioner.PlanAsync(p, _run, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Contains("Container Apps environment cae-shared " + text, ex.Message);
    }

    [Fact]
    public async Task Registry_provisioning_state_is_read_from_the_top_level()
    {
        On("acr show", Acr("crshared01", "rg-shared", "Updating"));
        var p = await ProfileAsync(d => d.ContainerRegistry = Existing("crshared01"));
        var ex = await Assert.ThrowsAsync<DeployerException>(() => _provisioner.PlanAsync(p, _run, CancellationToken.None));
        Assert.Contains("still being created", ex.Message);
    }

    private Task<DeployProfile> NewProfileAsync(Action<ProfileDestination>? more = null) => ProfileAsync(d =>
    {
        d.ResourceGroup = "rg-new";
        d.ContainerRegistry = new() { Mode = ResourceMode.New, Name = "crnew01", Sku = "Standard" };
        d.ContainerAppEnvironment = new() { Mode = ResourceMode.New, Name = "cae-new" };
        more?.Invoke(d);
    });

    private void NewResourcesMissing()
    {
        On("group exists", "false");
        On("acr show", GroupNotFound);
        On("acr check-name", Fixture("acr-check-name-free.json"));
        On("containerapp env show", GroupNotFound);
    }

    [Fact]
    public async Task New_resources_in_a_missing_group_plan_three_creates()
    {
        NewResourcesMissing();
        var plan = await _provisioner.PlanAsync(await NewProfileAsync(), _run, CancellationToken.None);

        Assert.Equal([AzureResourceKind.ResourceGroup, AzureResourceKind.ContainerRegistry, AzureResourceKind.ContainerAppEnvironment],
            plan.Select(s => s.Kind));
        Assert.All(plan, s => Assert.Equal(ProvisionAction.Create, s.Action));
        Assert.All(plan, s => Assert.Equal("rg-new", s.ResourceGroup));
        Assert.Equal("sku Standard", plan[1].Detail);
        Assert.Equal("logs: log-analytics", plan[2].Detail);
    }

    [Fact]
    public async Task Missing_group_is_refused_when_creation_is_not_allowed()
    {
        NewResourcesMissing();
        var ex = await Assert.ThrowsAsync<DeployerException>(async () =>
            await _provisioner.PlanAsync(await NewProfileAsync(d => d.AllowResourceGroupCreation = false), _run, CancellationToken.None));
        Assert.Equal("Resource group rg-new does not exist and the profile does not allow creating it.", ex.Message);
    }

    [Fact]
    public async Task Taken_registry_name_is_refused_before_anything_is_created()
    {
        On("group exists", "true");
        On("acr show", NotFound);
        On("acr check-name", Fixture("acr-check-name-taken.json"));
        var ex = await Assert.ThrowsAsync<DeployerException>(async () => await _provisioner.PlanAsync(await NewProfileAsync(), _run, CancellationToken.None));
        Assert.Equal("The registry name crnew01 is taken in Azure; choose another.", ex.Message);
        Assert.DoesNotContain(Calls, c => c.Contains(" create ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task New_names_already_there_are_not_created_again()
    {
        On("group exists", "true");
        On("acr show", Acr("crnew01", "rg-new"));
        On("containerapp env show", Env("cae-new", "rg-new"));
        var plan = await _provisioner.PlanAsync(await NewProfileAsync(), _run, CancellationToken.None);
        Assert.Equal([ProvisionAction.AlreadyThere, ProvisionAction.AlreadyThere], plan.Select(s => s.Action));
        Assert.DoesNotContain(Calls, c => c.StartsWith("acr check-name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_workspace_is_refused_in_the_plan()
    {
        NewResourcesMissing();
        On("monitor log-analytics workspace show", NotFound);
        var ex = await Assert.ThrowsAsync<DeployerException>(async () => await _provisioner.PlanAsync(await NewProfileAsync(d =>
        {
            d.ContainerAppEnvironment!.LogsDestination = "existing-workspace";
            d.ContainerAppEnvironment.LogsWorkspace = new() { Name = "ws-shared", ResourceGroup = "rg-logs" };
        }), _run, CancellationToken.None));
        Assert.Equal("Log Analytics workspace ws-shared was not found in rg-logs.", ex.Message);
    }

    [Fact]
    public async Task Existing_workspace_is_recorded_on_the_environment_step()
    {
        NewResourcesMissing();
        On("monitor log-analytics workspace show", JsonSerializer.Serialize(new { id = WorkspaceId, name = "ws-shared" }));
        var plan = await _provisioner.PlanAsync(await NewProfileAsync(d =>
        {
            d.ContainerAppEnvironment!.LogsDestination = "existing-workspace";
            d.ContainerAppEnvironment.LogsWorkspace = new() { Name = "ws-shared", ResourceGroup = "rg-logs" };
        }), _run, CancellationToken.None);
        var env = plan.Single(s => s.Kind == AzureResourceKind.ContainerAppEnvironment);
        Assert.Equal(WorkspaceId, env.WorkspaceId);
        Assert.Equal("logs: ws-shared", env.Detail);
    }

    private Task<DeployProfile> IdentityProfileAsync() => ProfileAsync(d =>
    {
        d.ContainerRegistry = Existing("crshared01");
        d.AcrPullIdentity = Existing("id-pull");
    });

    [Fact]
    public async Task Identity_without_AcrPull_is_refused_with_the_owner_command()
    {
        On("acr show", Fixture("acr-show.json"));
        On("identity show", Fixture("identity-show.json"));
        On("role assignment list", "[]");
        var ex = await Assert.ThrowsAsync<DeployerException>(async () => await _provisioner.PlanAsync(await IdentityProfileAsync(), _run, CancellationToken.None));
        Assert.Equal($"Identity id-pull cannot pull from crshared01. Ask an owner to run: az role assignment create --assignee 99999999-0000-0000-0000-000000000009 --role AcrPull --scope {RegistryId}",
            ex.Message);
        Assert.Contains(Calls, c => c.StartsWith($"role assignment list --assignee 99999999-0000-0000-0000-000000000009 --scope {RegistryId} --role AcrPull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Identity_check_that_is_not_allowed_does_not_block()
    {
        On("acr show", Fixture("acr-show.json"));
        On("identity show", Fixture("identity-show.json"));
        On("role assignment list", FakeProcessRunner.Fail(1, "ERROR: (AuthorizationFailed) The client does not have authorization"));
        var plan = await _provisioner.PlanAsync(await IdentityProfileAsync(), _run, CancellationToken.None);
        var id = plan.Single(s => s.Kind == AzureResourceKind.ManagedIdentity);
        Assert.Equal(ProvisionAction.UseExisting, id.Action);
        Assert.Equal("(could not check AcrPull)", id.Detail);
    }

    [Fact]
    public async Task Identity_with_AcrPull_is_used()
    {
        On("acr show", Fixture("acr-show.json"));
        On("identity show", Fixture("identity-show.json"));
        On("role assignment list", """[ { "roleDefinitionName": "AcrPull" } ]""");
        var plan = await _provisioner.PlanAsync(await IdentityProfileAsync(), _run, CancellationToken.None);
        Assert.Null(plan.Single(s => s.Kind == AzureResourceKind.ManagedIdentity).Detail);
    }

    [Fact]
    public async Task Unregistered_provider_gives_the_fix_line()
    {
        On("containerapp env show", FakeProcessRunner.Fail(1, "ERROR: (MissingSubscriptionRegistration) The subscription is not registered to use namespace 'Microsoft.App'."));
        var ex = await Assert.ThrowsAsync<DeployerException>(async () =>
            await _provisioner.PlanAsync(await ProfileAsync(d => d.ContainerAppEnvironment = Existing("cae-shared")), _run, CancellationToken.None));
        Assert.Contains("az provider register --namespace Microsoft.App", ex.Message);
    }

    // --- EnsureAsync ---

    private void CreatesSucceed()
    {
        On("group create", "{}");
        On("acr create", Acr("crnew01", "rg-new"));
        On("containerapp env create", Env("cae-new", "rg-new"));
    }

    private async Task<DeployProfile> StoredAsync() =>
        (await _h.Store.LoadAsync(CancellationToken.None)).Document.AspireDeploy.Profiles["shop"];

    [Fact]
    public async Task Ensure_creates_in_order_switches_the_profile_and_invalidates()
    {
        NewResourcesMissing();
        CreatesSucceed();
        var p = await NewProfileAsync();
        var plan = await _provisioner.PlanAsync(p, _run, CancellationToken.None);

        var done = await _provisioner.EnsureAsync("shop", p, plan, _run, CancellationToken.None);

        var creates = Calls.Where(c => c.Contains(" create ", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, creates.Count);
        Assert.StartsWith("group create --name rg-new --location eastus2", creates[0]);
        Assert.StartsWith("acr create --name crnew01 --resource-group rg-new --location eastus2 --sku Standard", creates[1]);
        Assert.StartsWith("containerapp env create --name cae-new --resource-group rg-new --location eastus2 --logs-destination log-analytics", creates[2]);
        Assert.All(creates, c => Assert.Contains("--tags created-by=AspireDeployService", c));
        Assert.Equal([Sub], _account.Invalidated);
        Assert.EndsWith("/managedEnvironments/cae-new", done.Single(s => s.Kind == AzureResourceKind.ContainerAppEnvironment).ResourceId);

        var d = (await StoredAsync()).Destination;
        Assert.Equal(ResourceMode.Existing, d.ContainerRegistry!.Mode);
        Assert.Equal("rg-new", d.ContainerRegistry.ResourceGroup);
        Assert.Null(d.ContainerRegistry.Sku);
        Assert.Equal(ResourceMode.Existing, d.ContainerAppEnvironment!.Mode);
        Assert.Null(d.ContainerAppEnvironment.LogsDestination);
        Assert.True(_h.Observer.Contains("Profile shop: container registry switched to Use existing (crnew01)."));
    }

    [Fact]
    public async Task Ensure_switches_a_New_name_found_already_there_without_creating()
    {
        On("group exists", "true");
        On("acr show", Acr("crnew01", "rg-new"));
        On("containerapp env show", Env("cae-new", "rg-new"));
        var p = await NewProfileAsync();
        var plan = await _provisioner.PlanAsync(p, _run, CancellationToken.None);
        await _provisioner.EnsureAsync("shop", p, plan, _run, CancellationToken.None);

        Assert.DoesNotContain(Calls, c => c.Contains(" create ", StringComparison.Ordinal));
        Assert.Empty(_account.Invalidated);
        var d = (await StoredAsync()).Destination;
        Assert.Equal(ResourceMode.Existing, d.ContainerRegistry!.Mode);
        Assert.Equal(ResourceMode.Existing, d.ContainerAppEnvironment!.Mode);
    }

    [Fact]
    public async Task Failed_registry_create_stops_before_the_environment_and_names_what_was_created()
    {
        NewResourcesMissing();
        On("group create", "{}");
        On("acr create", FakeProcessRunner.Fail(1, "ERROR: (QuotaExceeded) Too many registries."));
        var p = await NewProfileAsync();
        var plan = await _provisioner.PlanAsync(p, _run, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DeployerException>(() => _provisioner.EnsureAsync("shop", p, plan, _run, CancellationToken.None));

        Assert.Equal(OutcomeCode.DeployFailed, ex.Code);
        Assert.Contains("Could not create container registry crnew01: ERROR: (QuotaExceeded) Too many registries.", ex.Message);
        Assert.Contains("Already created: resource group rg-new.", ex.Message);
        Assert.DoesNotContain(Calls, c => c.StartsWith("containerapp env create", StringComparison.Ordinal));
        Assert.Equal([Sub], _account.Invalidated);
        Assert.Equal(ResourceMode.New, (await StoredAsync()).Destination.ContainerRegistry!.Mode);
    }

    [Fact]
    public async Task Conflict_during_create_says_another_run_created_it()
    {
        On("group exists", "true");
        On("acr show", NotFound);
        On("acr check-name", Fixture("acr-check-name-free.json"));
        On("containerapp env show", NotFound);
        On("acr create", FakeProcessRunner.Fail(1, "ERROR: (Conflict) The registry crnew01 is already in use."));
        var p = await NewProfileAsync();
        var plan = await _provisioner.PlanAsync(p, _run, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<DeployerException>(() => _provisioner.EnsureAsync("shop", p, plan, _run, CancellationToken.None));
        Assert.Equal(OutcomeCode.DeployFailed, ex.Code);
        Assert.StartsWith("Container registry crnew01 was created by another run; deploy again.", ex.Message);
    }

    private Task<DeployProfile> WorkspaceProfileAsync() => NewProfileAsync(d =>
    {
        d.ContainerRegistry = null;
        d.ContainerAppEnvironment!.LogsDestination = "existing-workspace";
        d.ContainerAppEnvironment.LogsWorkspace = new() { Name = "ws-shared", ResourceGroup = "rg-logs" };
    });

    [Fact]
    public async Task Existing_workspace_creates_with_azure_monitor_then_routes_logs()
    {
        NewResourcesMissing();
        CreatesSucceed();
        On("monitor log-analytics workspace show", JsonSerializer.Serialize(new { id = WorkspaceId, name = "ws-shared" }));
        On("monitor diagnostic-settings create", "{}");
        var p = await WorkspaceProfileAsync();
        var plan = await _provisioner.PlanAsync(p, _run, CancellationToken.None);
        await _provisioner.EnsureAsync("shop", p, plan, _run, CancellationToken.None);

        Assert.Contains(Calls, c => c.Contains("containerapp env create", StringComparison.Ordinal) && c.Contains("--logs-destination azure-monitor"));
        var routing = _h.AzSpecs.Single(s => FakeProcessRunner.Is(s, "monitor", "diagnostic-settings", "create")).Arguments.ToList();
        Assert.Equal(WorkspaceId, routing[routing.IndexOf("--workspace") + 1]);
        Assert.EndsWith("/managedEnvironments/cae-new", routing[routing.IndexOf("--resource") + 1]);
        Assert.Equal("""[{"categoryGroup":"allLogs","enabled":true}]""", routing[routing.IndexOf("--logs") + 1]);
        Assert.Null((await StoredAsync()).Destination.ContainerAppEnvironment!.LogsWorkspace);
    }

    [Fact]
    public async Task Failed_log_routing_stops_but_the_profile_has_switched()
    {
        NewResourcesMissing();
        CreatesSucceed();
        On("monitor log-analytics workspace show", JsonSerializer.Serialize(new { id = WorkspaceId, name = "ws-shared" }));
        On("monitor diagnostic-settings create", FakeProcessRunner.Fail(1, "ERROR: (AuthorizationFailed) nope"));
        var p = await WorkspaceProfileAsync();
        var plan = await _provisioner.PlanAsync(p, _run, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DeployerException>(() => _provisioner.EnsureAsync("shop", p, plan, _run, CancellationToken.None));

        Assert.Equal(OutcomeCode.DeployFailed, ex.Code);
        Assert.StartsWith("The environment was created, but its logs could not be sent to ws-shared: ERROR: (AuthorizationFailed) nope. Add a diagnostic setting in the portal.",
            ex.Message);
        Assert.Equal(ResourceMode.Existing, (await StoredAsync()).Destination.ContainerAppEnvironment!.Mode);
    }

    [Fact]
    public async Task Unwritable_settings_log_a_warning_and_continue()
    {
        NewResourcesMissing();
        CreatesSucceed();
        var p = await NewProfileAsync();
        var plan = await _provisioner.PlanAsync(p, _run, CancellationToken.None);
        File.WriteAllText(_h.SettingsPath, "{ not json");

        var done = await _provisioner.EnsureAsync("shop", p, plan, _run, CancellationToken.None);

        Assert.Equal(3, done.Count);
        Assert.True(_h.Observer.Contains("Warning: The profile could not be updated; set container registry to Use existing crnew01 yourself."));
    }

    // --- PreviewAsync ---

    private static readonly IReadOnlyList<DiscoveredResource> Compute =
    [
        new("apiservice", "project.v1", null, "a", false, true),
        new("webfrontend", "project.v1", null, "w", false, true)
    ];

    [Fact]
    public async Task Preview_for_a_new_environment_builds_its_id_and_treats_a_missing_group_as_empty()
    {
        On("containerapp list --subscription " + Sub + " --resource-group", GroupNotFound);
        On("containerapp list --subscription " + Sub + " --output", Fixture("containerapp-list.json"));
        var rows = await _provisioner.PreviewAsync(await NewProfileAsync(), Compute, null, _run, CancellationToken.None);
        Assert.Equal([AppPlanAction.New, AppPlanAction.New], rows.Select(r => r.Action));
    }

    [Fact]
    public async Task Preview_for_an_existing_environment_uses_the_plan_id_and_finds_conflicts_and_updates()
    {
        On("containerapp list --subscription " + Sub + " --resource-group rg-shop", Fixture("containerapp-list.json"));
        On("containerapp list --subscription " + Sub + " --output", Fixture("containerapp-list.json"));
        var p = await ProfileAsync(d => d.ContainerAppEnvironment = Existing("cae-shared"));
        var plan = new List<ProvisionStep> { new(AzureResourceKind.ContainerAppEnvironment, "cae-shared", "rg-shared", ProvisionAction.UseExisting, null, SharedEnvId) };

        var rows = await _provisioner.PreviewAsync(p, Compute, plan, _run, CancellationToken.None);

        Assert.DoesNotContain(Calls, c => c.StartsWith("containerapp env show", StringComparison.Ordinal));
        Assert.Equal(AppPlanAction.Conflict, rows[0].Action); // apiservice lives in rg-other in the shared environment
        Assert.Equal(AppPlanAction.Update, rows[1].Action);   // webfrontend is in rg-shop in that environment
    }

    [Fact]
    public async Task Preview_for_AspireDefault_infers_the_environment_from_the_group()
    {
        On("containerapp list --subscription " + Sub + " --resource-group rg-shop",
            JsonSerializer.Serialize(new[] { new { name = "webfrontend", resourceGroup = "rg-shop", properties = new { managedEnvironmentId = SharedEnvId } } }));
        On("containerapp list --subscription " + Sub + " --output", Fixture("containerapp-list.json"));
        var rows = await _provisioner.PreviewAsync(await ProfileAsync(_ => { }), Compute, null, _run, CancellationToken.None);
        Assert.Equal([AppPlanAction.Conflict, AppPlanAction.Update], rows.Select(r => r.Action));
    }

    [Fact]
    public async Task Preview_with_unreadable_apps_is_unknown()
    {
        On("containerapp list", FakeProcessRunner.Fail(1, "ERROR: something odd"));
        var rows = await _provisioner.PreviewAsync(await ProfileAsync(_ => { }), Compute, null, _run, CancellationToken.None);
        Assert.All(rows, r => Assert.Equal(AppPlanAction.Unknown, r.Action));
    }

    [Fact]
    public async Task Preview_always_reads_fresh_lists()
    {
        On("containerapp list", "[]");
        var p = await ProfileAsync(_ => { });
        await _provisioner.PreviewAsync(p, Compute, null, _run, CancellationToken.None);
        await _provisioner.PreviewAsync(p, Compute, null, _run, CancellationToken.None);
        Assert.Equal(2, Calls.Count(c => c.StartsWith("containerapp list", StringComparison.Ordinal)));
    }

    /// <summary>Records Invalidate; the list methods are not used by the provisioner.</summary>
    private sealed class SpyAccount : IAzureAccountService
    {
        public List<string> Invalidated { get; } = [];
        public void Invalidate(string subscriptionId) => Invalidated.Add(subscriptionId);
        public void InvalidateAll() => Invalidated.Add("*");
        public Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureLocation>> ListLocationsAsync(string s, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureResourceGroup>> ListResourceGroupsAsync(string s, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureContainerRegistry>> ListContainerRegistriesAsync(string s, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureContainerAppEnvironment>> ListContainerAppEnvironmentsAsync(string s, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureContainerApp>> ListContainerAppsAsync(string s, string? g, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<NameAvailability> CheckRegistryNameAsync(string s, string n, IRunObserver o, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureManagedIdentity>> ListManagedIdentitiesAsync(string s, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureLogAnalyticsWorkspace>> ListLogAnalyticsWorkspacesAsync(string s, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
    }
}
