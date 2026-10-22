using AspireDeployService.Core.Azure;
using AspireDeployService.Core.Deploy;
using AspireDeployService.Core.Discovery;

namespace AspireDeployService.Tests.Deploy;

/// <summary>Resource pickers plan §6.6 rules, first match wins.</summary>
public class ContainerAppPlannerTests
{
    private const string Env = "/subscriptions/s/resourceGroups/rg-shared/providers/Microsoft.App/managedEnvironments/cae-shared";
    private const string OtherEnv = "/subscriptions/s/resourceGroups/rg-x/providers/Microsoft.App/managedEnvironments/cae-other";

    private static readonly DiscoveredResource Web = new("webfrontend", "project.v1", null, "Web.csproj", false, true);

    private static AppPlanRow One(AppPlanInput input, DiscoveredResource? r = null) =>
        Assert.Single(ContainerAppPlanner.Plan([r ?? Web], input));

    [Fact]
    public void Only_compute_resources_are_planned()
    {
        var rows = ContainerAppPlanner.Plan([
            Web,
            new("cache", "container.v0", "redis", null, false, false),
            new("worker", "dockerfile.v0", null, "w", false, true),
            new("env", "azure.bicep.v0", null, null, true, false),
            new("p", "parameter.v0", null, null, false, false)
        ], new AppPlanInput("rg-shop", null, [], []));
        Assert.Equal(["webfrontend", "cache", "worker"], rows.Select(r => r.AppName));
    }

    [Fact]
    public void App_name_is_the_lowercase_resource_name() =>
        Assert.Equal("webfront", One(new("rg-shop", null, [], []), Web with { Name = "WebFront" }).AppName);

    [Fact]
    public void Invalid_name_is_a_conflict()
    {
        var row = One(new("rg-shop", null, [], []), Web with { Name = "this-name-is-far-too-long-for-a-container-app" });
        Assert.Equal(AppPlanAction.Conflict, row.Action);
        Assert.Equal("Aspire will shorten or reject this name.", row.Note);
    }

    [Fact]
    public void Unreadable_lists_are_unknown()
    {
        Assert.Equal(AppPlanAction.Unknown, One(new("rg-shop", Env, null, [])).Action);
        Assert.Equal(AppPlanAction.Unknown, One(new("rg-shop", Env, [], null)).Action);
        Assert.Equal("webfrontend (could not check)", ContainerAppPlanner.Describe(One(new("rg-shop", Env, null, []))));
    }

    [Fact]
    public void Same_group_and_environment_is_an_update()
    {
        var app = new AzureContainerApp("webfrontend", "rg-shop", Env.ToUpperInvariant());
        Assert.Equal(AppPlanAction.Update, One(new("RG-SHOP", Env, [app], [app])).Action);
    }

    [Fact]
    public void Same_group_with_unknown_environment_is_an_update() =>
        Assert.Equal(AppPlanAction.Update, One(new("rg-shop", null, [new("webfrontend", "rg-shop", OtherEnv)], [])).Action);

    [Fact]
    public void Same_group_other_environment_is_a_conflict()
    {
        var row = One(new("rg-shop", Env, [new("webfrontend", "rg-shop", OtherEnv)], []));
        Assert.Equal(AppPlanAction.Conflict, row.Action);
        Assert.Contains("already runs in environment cae-other", row.Note);
    }

    [Fact]
    public void Same_name_in_the_shared_environment_from_another_group_is_a_conflict()
    {
        var row = One(new("rg-shop", Env, [], [new("webfrontend", "rg-team2", Env)]));
        Assert.Equal(AppPlanAction.Conflict, row.Action);
        Assert.Equal("webfrontend in rg-team2 already uses this name in the shared environment.", row.Note);
    }

    [Fact]
    public void Otherwise_new()
    {
        var row = One(new("rg-shop", Env, [new("apiservice", "rg-shop", Env)], [new("other", "rg-x", Env)]));
        Assert.Equal(AppPlanAction.New, row.Action);
        Assert.Equal("webfrontend (new)", ContainerAppPlanner.Describe(row));
    }
}
