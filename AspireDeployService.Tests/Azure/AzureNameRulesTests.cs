using AspireDeployService.Core.Azure;

namespace AspireDeployService.Tests.Azure;

public class AzureNameRulesTests
{
    [Theory]
    [InlineData("crshared01", true)]
    [InlineData("abcde", true)]
    [InlineData("abcd", false)]          // too short
    [InlineData("CrShared01", false)]    // uppercase (D9)
    [InlineData("cr-shared", false)]     // dash
    public void Registry_names(string name, bool valid) =>
        Assert.Equal(valid, AzureNameRules.Problem(AzureResourceKind.ContainerRegistry, name) is null);

    [Fact]
    public void Uppercase_registry_name_says_lowercase() =>
        Assert.Contains("lowercase", AzureNameRules.Problem(AzureResourceKind.ContainerRegistry, "CrShared01")!);

    [Fact]
    public void Registry_name_of_50_is_valid_and_51_is_not()
    {
        Assert.Null(AzureNameRules.Problem(AzureResourceKind.ContainerRegistry, new string('a', 50)));
        Assert.NotNull(AzureNameRules.Problem(AzureResourceKind.ContainerRegistry, new string('a', 51)));
    }

    [Theory]
    [InlineData("cae-shop", true)]
    [InlineData("ab", true)]
    [InlineData("cae--shop", false)]
    [InlineData("1cae", false)]
    [InlineData("cae-", false)]
    [InlineData("Cae", false)]
    public void Environment_names(string name, bool valid) =>
        Assert.Equal(valid, AzureNameRules.Problem(AzureResourceKind.ContainerAppEnvironment, name) is null);

    [Fact]
    public void Environment_name_of_60_is_valid_and_61_is_not()
    {
        Assert.Null(AzureNameRules.Problem(AzureResourceKind.ContainerAppEnvironment, "a" + new string('b', 59)));
        Assert.NotNull(AzureNameRules.Problem(AzureResourceKind.ContainerAppEnvironment, "a" + new string('b', 60)));
    }

    [Theory]
    [InlineData("webfrontend", true)]
    [InlineData("a" + "bcdefghijklmnopqrstuvwxyz012345", true)] // 32
    [InlineData("a" + "bcdefghijklmnopqrstuvwxyz0123456", false)] // 33
    [InlineData("web--front", false)]
    public void Container_app_names(string name, bool valid) =>
        Assert.Equal(valid, AzureNameRules.Problem(AzureResourceKind.ContainerApp, name) is null);

    [Fact]
    public void Resource_group_rule_is_unchanged()
    {
        Assert.Null(AzureNameRules.Problem(AzureResourceKind.ResourceGroup, "rg-shop.(1)"));
        Assert.NotNull(AzureNameRules.Problem(AzureResourceKind.ResourceGroup, "rg-shop."));
    }

    [Fact]
    public void Picked_kinds_are_never_refused()
    {
        Assert.Null(AzureNameRules.Problem(AzureResourceKind.ManagedIdentity, null));
        Assert.Null(AzureNameRules.Problem(AzureResourceKind.LogAnalyticsWorkspace, "ANY thing"));
    }

    [Fact]
    public void Empty_name_is_refused() =>
        Assert.Equal("Enter a name.", AzureNameRules.Problem(AzureResourceKind.ContainerRegistry, " "));

    [Fact]
    public void Suggestions_follow_the_plan_table()
    {
        Assert.Equal("crmyapp", AzureNameRules.Suggest(AzureResourceKind.ContainerRegistry, "my-app"));
        Assert.Equal("cae-my-app", AzureNameRules.Suggest(AzureResourceKind.ContainerAppEnvironment, "my-app"));
        Assert.Equal("rg-my-app", AzureNameRules.Suggest(AzureResourceKind.ResourceGroup, "my-app"));
        Assert.Equal("crmyapp1a2b", AzureNameRules.Suggest(AzureResourceKind.ContainerRegistry, "my-app", "1a2b"));
        Assert.Equal("cae-my-app-1a2b", AzureNameRules.Suggest(AzureResourceKind.ContainerAppEnvironment, "my-app", "1a2b"));
    }

    [Fact]
    public void Short_registry_suggestion_is_padded_to_a_valid_name()
    {
        var name = AzureNameRules.Suggest(AzureResourceKind.ContainerRegistry, "a");
        Assert.StartsWith("cra", name);
        Assert.Null(AzureNameRules.Problem(AzureResourceKind.ContainerRegistry, name));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("my-very-long-profile-name-that-goes-on-and-on-forever")]
    [InlineData("9lives")]
    public void Every_suggestion_is_valid(string profile)
    {
        Assert.Null(AzureNameRules.Problem(AzureResourceKind.ContainerRegistry, AzureNameRules.Suggest(AzureResourceKind.ContainerRegistry, profile + profile + profile)));
        Assert.Null(AzureNameRules.Problem(AzureResourceKind.ContainerAppEnvironment, AzureNameRules.Suggest(AzureResourceKind.ContainerAppEnvironment, profile + profile)));
    }

    [Fact]
    public void Describe_lists_four_kinds_and_only_registry_is_global()
    {
        var rows = AzureNameRules.Describe();
        Assert.Equal(4, rows.Count);
        Assert.Equal([AzureResourceKind.ContainerRegistry], rows.Where(r => r.GloballyUnique).Select(r => r.Kind));
    }
}
