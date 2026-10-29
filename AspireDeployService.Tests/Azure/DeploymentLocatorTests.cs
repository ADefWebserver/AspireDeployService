using AspireDeployService.Core.Azure;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Sources;

// Test shapes adapted from BlazorDataDeploymentWebsite.Tests/Deployments/InstallationDiscoveryTests.cs (wizard plan D16):
// AzureContainerApp records instead of JsonElement rows, and the real azd up case with an unrelated app kept.
namespace AspireDeployService.Tests.Azure;

public class DeploymentLocatorTests
{
    private const string Sub = "00000000-0000-0000-0000-000000000001";

    private static readonly string[] Compute = ["apiservice", "webfrontend", "worker"];
    private static readonly string[] EnvNames = ["env"];

    private static string EnvId(string group, string name) =>
        $"/subscriptions/{Sub}/resourceGroups/{group}/providers/Microsoft.App/managedEnvironments/{name}";

    private static AzureContainerApp App(string name, string group, string envName, string? location = "eastus2") =>
        new(name, group, EnvId(group, envName), location);

    private static AzureContainerAppEnvironment Env(string group, string name, string location = "East US 2") =>
        new(EnvId(group, name), name, group, location);

    private static AzureResourceGroup Group(string name, string location = "eastus2", params (string Key, string Value)[] tags) =>
        new(name, location, tags.Length == 0 ? null : tags.ToDictionary(t => t.Key, t => t.Value));

    private static IReadOnlyList<DeploymentCandidate> Rank(IReadOnlyList<AzureContainerApp> apps,
        IReadOnlyList<AzureContainerAppEnvironment>? envs = null, IReadOnlyList<AzureResourceGroup>? groups = null,
        IReadOnlyList<string>? envNames = null) =>
        DeploymentLocator.Rank(Sub, Compute, envNames ?? EnvNames, apps, envs ?? [], groups ?? []);

    [Fact]
    public void Rank_groups_by_group_and_environment()
    {
        var candidates = Rank(
            [App("apiservice", "rg-b", "envtoken1"), App("webfrontend", "rg-b", "envtoken1"), App("apiservice", "rg-a", "envtoken2")],
            [Env("rg-b", "envtoken1"), Env("rg-a", "envtoken2", "westeurope")],
            [Group("rg-a", "westus"), Group("rg-b")]);

        Assert.Equal(["rg-b", "rg-a"], candidates.Select(c => c.ResourceGroup));
        var first = candidates[0];
        Assert.Equal(["apiservice", "webfrontend"], first.Found);
        Assert.Equal(["worker"], first.Missing);
        Assert.Equal("envtoken1", first.EnvironmentName);
        Assert.Equal("rg-b", first.EnvironmentResourceGroup);
        Assert.Equal("eastus2", first.Location);
        Assert.False(first.PinEnvironment);
        Assert.Empty(first.Warnings);
        Assert.Equal("westeurope", candidates[1].Location);
    }

    [Fact]
    public void Rank_location_falls_back_to_the_app_then_the_group()
    {
        var fromApp = Assert.Single(Rank([App("apiservice", "rg", "envx", "northeurope")], groups: [Group("rg", "westus")]));
        Assert.Equal("northeurope", fromApp.Location);
        var fromGroup = Assert.Single(Rank([App("apiservice", "rg", "envx", null)], groups: [Group("rg", "westus")]));
        Assert.Equal("westus", fromGroup.Location);
    }

    [Fact]
    public void Rank_marks_azd_and_aspire_origins()
    {
        var candidates = Rank(
            [App("apiservice", "rg-aspire", "envaaa"), App("apiservice", "rg-azd-ok", "envbbb"), App("apiservice", "rg-azd-bad", "cae-ccc"),
             App("apiservice", "rg-plain", "envddd")],
            groups:
            [
                Group("rg-aspire", tags: ("aspire", "true")),
                Group("rg-azd-ok", tags: ("azd-env-name", "dev")),
                Group("rg-azd-bad", tags: ("azd-env-name", "dev")),
                Group("rg-plain")
            ]);
        var by = candidates.ToDictionary(c => c.ResourceGroup);

        Assert.Equal(DeploymentOrigin.AspireDeploy, by["rg-aspire"].Origin);
        Assert.Equal(DeploymentOrigin.Unknown, by["rg-plain"].Origin);
        Assert.Equal(DeploymentOrigin.Azd, by["rg-azd-ok"].Origin);
        Assert.DoesNotContain(DeploymentLocator.AzdWarning, by["rg-azd-ok"].Warnings);
        Assert.False(by["rg-azd-ok"].PinEnvironment);
        Assert.Equal(DeploymentOrigin.Azd, by["rg-azd-bad"].Origin);
        Assert.Contains(DeploymentLocator.AzdWarning, by["rg-azd-bad"].Warnings);
        Assert.True(by["rg-azd-bad"].PinEnvironment);
    }

    [Fact]
    public void Rank_ignores_an_unrelated_app_in_another_environment()
    {
        // After BlazorDataDeploymentWebsite's real azd up case: Aspire sets no azd-service-name tag, so apps are found by
        // name; an unrelated app in another group and environment must give no candidate.
        var candidates = Rank(
            [App("apiservice", "rg-starter", "envhqy5c5p4nqgmg"), App("webfrontend", "rg-starter", "envhqy5c5p4nqgmg"),
             App("worker", "rg-starter", "envhqy5c5p4nqgmg"), App("promoter-api", "rg-other", "promoterenv")],
            groups: [Group("rg-starter", tags: ("azd-env-name", "starter")), Group("rg-other")]);

        var only = Assert.Single(candidates);
        Assert.Equal("rg-starter", only.ResourceGroup);
        Assert.Equal(3, only.Found.Count);
        Assert.Empty(only.Missing);
        Assert.Empty(only.Warnings);
    }

    [Fact]
    public void NameFromId_and_ResourceGroupFromId()
    {
        Assert.Equal("cae", DeploymentLocator.NameFromId(EnvId("rg", "cae") + "/"));
        Assert.Equal("rg-x", DeploymentLocator.ResourceGroupFromId($"/subscriptions/{Sub}/RESOURCEGROUPS/rg-x/providers/a/b/c"));
        Assert.Null(DeploymentLocator.ResourceGroupFromId("/subscriptions/x/resourceGroups"));
        Assert.Null(DeploymentLocator.ResourceGroupFromId("not-an-id"));
    }

    [Fact]
    public void Rank_pins_environment_when_name_does_not_match()
    {
        var c = Assert.Single(Rank([App("apiservice", "rg", "cae-token")]));
        Assert.True(c.PinEnvironment);
        Assert.Contains(DeploymentLocator.NamingWarning, c.Warnings);
        Assert.DoesNotContain(DeploymentLocator.AzdWarning, c.Warnings);
    }

    [Fact]
    public void Rank_naming_check_ignores_case_and_punctuation()
    {
        var c = Assert.Single(Rank([App("apiservice", "rg", "myenv123token")], envNames: ["My-Env"]));
        Assert.False(c.PinEnvironment);
    }

    [Fact]
    public void Rank_pins_each_environment_of_a_mixed_group()
    {
        var candidates = Rank([App("apiservice", "rg", "envone"), App("webfrontend", "rg", "envtwo")]);
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c =>
        {
            Assert.True(c.PinEnvironment);
            Assert.Contains(DeploymentLocator.MixedWarning, c.Warnings);
        });
    }

    [Fact]
    public void Rank_without_an_environment_does_not_pin()
    {
        var c = Assert.Single(Rank([new AzureContainerApp("apiservice", "rg", null)]));
        Assert.Null(c.EnvironmentId);
        Assert.False(c.PinEnvironment);
        Assert.Equal([DeploymentLocator.NoEnvironmentWarning], c.Warnings);
    }

    [Fact]
    public void Rank_ignores_apps_not_in_the_apphost()
    {
        Assert.Empty(Rank([App("apiservice2", "rg", "env1"), App("api", "rg", "env1")]));
        var c = Assert.Single(Rank([new AzureContainerApp("ApiService", "rg", EnvId("rg", "env1"))]));
        Assert.Equal(["apiservice"], c.Found);
    }

    [Fact]
    public void Rank_caps_at_20()
    {
        var apps = Enumerable.Range(0, 25).Select(i => App("apiservice", $"rg-{i:00}", "envx")).ToList();
        var candidates = Rank(apps);
        Assert.Equal(DeploymentLocator.MaxCandidates, candidates.Count);
        Assert.Equal("rg-00", candidates[0].ResourceGroup);
    }

    [Fact]
    public void Parsed_fixtures_rank_together()
    {
        var apps = AzureAccountService.ParseContainerApps(AzureAccountListTests.Az("containerapp-list-located.json"));
        var groups = AzureAccountService.ParseResourceGroups(AzureAccountListTests.Az("group-list-tags.json"));
        var c = Assert.Single(DeploymentLocator.Rank(Sub, Compute, EnvNames, apps, [], groups));
        Assert.Equal(("rg-starter", "eastus2", DeploymentOrigin.AspireDeploy), (c.ResourceGroup, c.Location, c.Origin));
        Assert.Equal(["apiservice", "webfrontend"], c.Found);
    }

    [Fact]
    public void Captured_aspire_deploy_of_Blazor_Azure_Inventory_is_found()
    {
        // T0: a real aspire deploy (13.6.1) of ADefWebserver/Blazor-Azure-Inventory. Its AppHost renames the web app to
        // azinventory-web and names its environment "aca-env"; Aspire tags the group aspire=true.
        var apps = AzureAccountService.ParseContainerApps(AzureAccountListTests.Az("containerapp-list-captured.json"));
        var envs = AzureAccountService.ParseEnvironments(AzureAccountListTests.Az("containerapp-env-list-captured.json"));
        var groups = AzureAccountService.ParseResourceGroups(AzureAccountListTests.Az("group-list-captured.json"));

        var c = Assert.Single(DeploymentLocator.Rank(Sub, ["azure-mcp", "azinventory-web"], ["aca-env"], apps, envs, groups));
        Assert.Equal(("rg-azinv-test", "eastus2", "acaenvzd3n7pq724i34"), (c.ResourceGroup, c.Location, c.EnvironmentName));
        Assert.Equal(DeploymentOrigin.AspireDeploy, c.Origin);
        Assert.Equal(2, c.Found.Count);
        Assert.False(c.PinEnvironment);
        Assert.Empty(c.Warnings);
    }

    [Theory]
    [InlineData("az containerapp list failed: ERROR: (AuthorizationFailed) The client does not have authorization", "Reader access")]
    [InlineData("az containerapp list failed: ERROR: AADSTS50076: use multi-factor authentication.", "not signed in")]
    [InlineData("az containerapp list failed: ERROR: something odd", "something odd")]
    public void DescribeFailure_by_kind(string message, string expected) =>
        Assert.Contains(expected, DeploymentLocator.DescribeFailure(message));

    [Fact]
    public async Task FindAsync_reads_fresh_lists()
    {
        var spy = new ListSpy([App("apiservice", "rg", "envx")]);
        var found = await DeploymentLocator.FindAsync(spy, Sub, Discovery(), NullRunObserver.Instance, CancellationToken.None);
        Assert.Single(found);
        Assert.Equal(["apps:*:True", "environments:True", "groups:True"], spy.Calls);
    }

    [Fact]
    public async Task FindAsync_without_compute_is_invalid_input()
    {
        var discovery = Discovery() with { Resources = [new DiscoveredResource("db", "azure.bicep.v0", null, null, true, false)] };
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            DeploymentLocator.FindAsync(new ListSpy([]), Sub, discovery, NullRunObserver.Instance, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Equal("This AppHost has no compute resources to look for.", ex.Message);
    }

    private static DiscoveryResult Discovery() => new(
        new AppHostInfo("AppHost/AppHost.csproj", AppHostKind.ProjectCsproj, "13.6.1", [], [DeploymentTarget.AzureContainerApps], null)
        {
            ContainerAppEnvironmentNames = ["env"]
        },
        new SourceIdentity("c:/src", "c:/src", null),
        [],
        [new DiscoveredResource("apiservice", "project.v1", null, "Api/Api.csproj", false, false)],
        [],
        ManifestRan: true);

    private sealed class ListSpy(IReadOnlyList<AzureContainerApp> apps) : IAzureAccountService
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<AzureContainerApp>> ListContainerAppsAsync(string s, string? g, IRunObserver o, CancellationToken ct, bool r = false)
        {
            Calls.Add($"apps:{g ?? "*"}:{r}");
            return Task.FromResult(apps);
        }
        public Task<IReadOnlyList<AzureContainerAppEnvironment>> ListContainerAppEnvironmentsAsync(string s, IRunObserver o, CancellationToken ct, bool r = false)
        {
            Calls.Add($"environments:{r}");
            return Task.FromResult<IReadOnlyList<AzureContainerAppEnvironment>>([]);
        }
        public Task<IReadOnlyList<AzureResourceGroup>> ListResourceGroupsAsync(string s, IRunObserver o, CancellationToken ct, bool r = false)
        {
            Calls.Add($"groups:{r}");
            return Task.FromResult<IReadOnlyList<AzureResourceGroup>>([]);
        }
        public void Invalidate(string subscriptionId) { }
        public void InvalidateAll() { }
        public event Action? Changed { add { } remove { } }
        public Task SetDefaultSubscriptionAsync(string s, IRunObserver o, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureLocation>> ListLocationsAsync(string s, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureContainerRegistry>> ListContainerRegistriesAsync(string s, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<NameAvailability> CheckRegistryNameAsync(string s, string n, IRunObserver o, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureManagedIdentity>> ListManagedIdentitiesAsync(string s, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureLogAnalyticsWorkspace>> ListLogAnalyticsWorkspacesAsync(string s, IRunObserver o, CancellationToken ct, bool r = false) => throw new NotSupportedException();
    }
}
