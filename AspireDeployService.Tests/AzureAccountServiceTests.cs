using AspireDeployService.Core.Azure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests;

public class AzureAccountServiceTests : IDisposable
{
    private const string Sub = "00000000-0000-0000-0000-000000000001";

    private const string Subscriptions = """
        [ { "id": "11111111-0000-0000-0000-000000000000", "name": "Zeta", "tenantId": "t1", "isDefault": false, "state": "Disabled" },
          { "id": "00000000-0000-0000-0000-000000000001", "name": "Main", "tenantId": "t2", "isDefault": true, "state": "Enabled" },
          { "id": "22222222-0000-0000-0000-000000000000", "name": "Alpha", "tenantId": "t1", "isDefault": false } ]
        """;

    private const string Locations = """
        [ { "name": "westus", "displayName": "West US", "metadata": { "regionType": "Physical", "geographyGroup": "US" } },
          { "name": "eastus2", "displayName": "East US 2", "metadata": { "regionType": "Physical", "geographyGroup": "US" } },
          { "name": "unitedstates", "displayName": "United States", "metadata": { "regionType": "Logical" } },
          { "name": "eastus2euap", "displayName": "East US 2 EUAP", "metadata": { "regionType": "Physical" } } ]
        """;

    private const string Provider = """
        { "namespace": "Microsoft.App", "resourceTypes": [
            { "resourceType": "containerApps", "locations": [ "West US" ] },
            { "resourceType": "managedEnvironments", "locations": [ "East US 2", "East US 2 EUAP" ] } ] }
        """;

    private readonly TempDir _dir = new();
    private readonly List<ProcessSpec> _calls = [];
    private readonly AzureAccountService _service;

    public AzureAccountServiceTests()
    {
        var path = _dir.Write("appsettings.Local.json", $$"""{ "AspireDeploy": { "WorkRoot": "{{_dir.Combine("w").Replace("\\", "/")}}" } }""");
        var runner = new FakeProcessRunner((spec, _) =>
        {
            lock (_calls)
                _calls.Add(spec);
            var args = string.Join(' ', spec.Arguments);
            return Task.FromResult(args switch
            {
                _ when args.StartsWith("account list ") => FakeProcessRunner.Ok(Subscriptions),
                _ when args.StartsWith($"rest --method get --url https://management.azure.com/subscriptions/{Sub}/locations?api-version=2022-12-01")
                    => FakeProcessRunner.Ok("{ \"value\": " + Locations + " }"),
                _ when args.StartsWith("provider show") => FakeProcessRunner.Ok(Provider),
                _ when args.StartsWith("group list") => FakeProcessRunner.Ok("""[ { "name": "rg-b", "location": "eastus2" }, { "name": "rg-a", "location": "westus" } ]"""),
                _ => FakeProcessRunner.Fail(1, "ERROR: unexpected")
            });
        });
        _service = new AzureAccountService(new ProfileStore(new FixedLocator(path)), new FakeRunContextFactory(runner), TimeProvider.System);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Subscriptions_put_the_default_first_and_flag_unusable_ones()
    {
        var list = await _service.ListSubscriptionsAsync(NullRunObserver.Instance, CancellationToken.None);
        Assert.Equal(["Main", "Alpha", "Zeta"], list.Select(s => s.Name));
        Assert.True(list[1].IsUsable); // no state means Enabled
        Assert.False(list[2].IsUsable);
    }

    [Fact]
    public async Task Locations_are_physical_only_and_marked_for_container_apps()
    {
        var list = await _service.ListLocationsAsync(Sub, NullRunObserver.Instance, CancellationToken.None);
        Assert.Equal(["eastus2", "eastus2euap", "westus"], list.Select(l => l.Name));
        Assert.True(list[0].SupportsContainerApps);
        Assert.False(list[2].SupportsContainerApps); // only containerApps, not managedEnvironments
        Assert.Equal("US", list[0].Geography);
    }

    [Fact]
    public void Container_apps_support_is_unknown_without_the_provider()
    {
        Assert.All(AzureAccountService.ParseLocations(Locations, null), l => Assert.Null(l.SupportsContainerApps));
    }

    [Fact]
    public async Task Resource_groups_are_sorted_and_results_are_cached_per_subscription()
    {
        var first = await _service.ListResourceGroupsAsync(Sub, NullRunObserver.Instance, CancellationToken.None);
        await _service.ListResourceGroupsAsync(Sub, NullRunObserver.Instance, CancellationToken.None);
        Assert.Equal(["rg-a", "rg-b"], first.Select(g => g.Name));
        Assert.Single(_calls, c => c.Arguments[0] == "group");

        await _service.ListResourceGroupsAsync(Sub, NullRunObserver.Instance, CancellationToken.None, refresh: true);
        Assert.Equal(2, _calls.Count(c => c.Arguments[0] == "group"));
        Assert.Contains(Sub, _calls[0].Arguments);
    }

    [Fact]
    public async Task A_subscription_that_is_not_a_guid_is_refused_before_running_az()
    {
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _service.ListResourceGroupsAsync("not-a-guid", NullRunObserver.Instance, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Empty(_calls);
    }
}
