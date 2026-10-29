using System.Text.Json;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Services;
using AspireDeployService.Core.Settings;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests.Azure;

/// <summary>Resource pickers plan §6.1 and §11: the stored shape, the rules, ApplySet keys and ApplyTo.</summary>
public class ResourceChoiceSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private DeployProfile Profile(Action<ProfileDestination>? change = null)
    {
        var p = ProfileStoreTests.ValidProfile(_dir.Path);
        change?.Invoke(p.Destination);
        return p;
    }

    private static ResourceChoice Existing(string name, string? rg = "rg-shared") => new() { Mode = ResourceMode.Existing, Name = name, ResourceGroup = rg };

    // --- round trip ---

    [Fact]
    public async Task A_file_without_the_new_keys_loads_as_AspireDefault_and_saves_without_them()
    {
        var path = _dir.Combine("appsettings.Local.json");
        File.WriteAllText(path, $$"""
            { "AspireDeploy": { "Profiles": { "shop": {
              "Source": { "Kind": "LocalFolder", "Location": "{{_dir.Path.Replace("\\", "\\\\")}}" },
              "Destination": { "SubscriptionId": "00000000-0000-0000-0000-000000000001", "Location": "eastus2", "ResourceGroup": "rg-shop" } } } } }
            """);
        var store = new ProfileStore(new FixedLocator(path));
        var doc = (await store.LoadAsync(CancellationToken.None)).Document;
        var d = doc.AspireDeploy.Profiles["shop"].Destination;
        Assert.Null(d.ContainerRegistry);
        Assert.Equal(ResourceMode.AspireDefault, d.RegistryMode);
        Assert.Equal(ResourceMode.AspireDefault, d.EnvironmentMode);
        Assert.Equal(ResourceMode.AspireDefault, d.IdentityMode);
        Assert.False(d.NeedsOverlay);

        await store.UpdateAsync(_ => { }, CancellationToken.None);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("ContainerRegistry", text);
        Assert.DoesNotContain("ContainerAppEnvironment", text);
        Assert.DoesNotContain("AcrPullIdentity", text);
    }

    [Fact]
    public void Modes_serialize_as_names_and_unknown_keys_survive()
    {
        var json = """{ "Mode": "Existing", "Name": "crshared01", "Future": 42, "LogsWorkspace": { "Name": "ws", "Other": true } }""";
        var choice = JsonSerializer.Deserialize<ResourceChoice>(json, SettingsJson.Options)!;
        Assert.Equal(ResourceMode.Existing, choice.EffectiveMode);
        var back = JsonSerializer.Serialize(choice, SettingsJson.Options);
        Assert.Contains("\"Mode\": \"Existing\"", back);
        Assert.Contains("\"Future\": 42", back);
        Assert.Contains("\"Other\": true", back);
    }

    [Fact]
    public void Defaults_for_sku_and_logs()
    {
        var c = new ResourceChoice();
        Assert.Equal("Basic", c.EffectiveSku);
        Assert.Equal("log-analytics", c.EffectiveLogsDestination);
        Assert.Equal(ResourceMode.AspireDefault, c.EffectiveMode);
    }

    // --- Validate ---

    private IReadOnlyList<string> Errors(Action<ProfileDestination> change) => ProfileRules.Validate(Profile(change));

    [Fact]
    public void All_AspireDefault_is_valid() => Assert.Empty(ProfileRules.Validate(Profile()));

    [Fact]
    public void Existing_or_new_without_name_is_refused()
    {
        Assert.Contains("Destination:ContainerRegistry:Name: choose or enter a registry.",
            Errors(d => d.ContainerRegistry = new() { Mode = ResourceMode.Existing }));
        Assert.Contains("Destination:ContainerAppEnvironment:Name: choose or enter an environment.",
            Errors(d => d.ContainerAppEnvironment = new() { Mode = ResourceMode.New }));
    }

    [Fact]
    public void Invalid_names_give_the_rule_text()
    {
        Assert.Contains(Errors(d => d.ContainerRegistry = new() { Mode = ResourceMode.New, Name = "Cr-Bad" }),
            e => e.StartsWith("Destination:ContainerRegistry:Name: ", StringComparison.Ordinal));
        Assert.Contains(Errors(d => d.ContainerAppEnvironment = new() { Mode = ResourceMode.New, Name = "cae--x" }),
            e => e.StartsWith("Destination:ContainerAppEnvironment:Name: ", StringComparison.Ordinal));
    }

    [Fact]
    public void An_existing_environment_keeps_its_capitals_but_a_new_one_must_be_lowercase()
    {
        Assert.Empty(Errors(d => d.ContainerAppEnvironment = new() { Mode = ResourceMode.Existing, Name = "LACOEContainerAppEnvDev", ResourceGroup = "rg-x" }));
        Assert.Contains(Errors(d => d.ContainerAppEnvironment = new() { Mode = ResourceMode.Existing, Name = "Cae--X", ResourceGroup = "rg-x" }),
            e => e.StartsWith("Destination:ContainerAppEnvironment:Name: ", StringComparison.Ordinal));
        Assert.Contains(Errors(d => d.ContainerAppEnvironment = new() { Mode = ResourceMode.New, Name = "LACOEContainerAppEnvDev" }),
            e => e.StartsWith("Destination:ContainerAppEnvironment:Name: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Bad_sku_and_logs_destination_are_refused()
    {
        Assert.Contains("Destination:ContainerRegistry:Sku: Basic, Standard or Premium.",
            Errors(d => d.ContainerRegistry = new() { Mode = ResourceMode.New, Name = "crshop01", Sku = "Gold" }));
        Assert.Contains("Destination:ContainerAppEnvironment:LogsDestination: log-analytics, existing-workspace or none.",
            Errors(d => d.ContainerAppEnvironment = new() { Mode = ResourceMode.New, Name = "cae-shop", LogsDestination = "splunk" }));
    }

    [Fact]
    public void Existing_workspace_needs_both_fields()
    {
        Assert.Contains("Destination:ContainerAppEnvironment:LogsWorkspace: choose a Log Analytics workspace.",
            Errors(d => d.ContainerAppEnvironment = new()
            {
                Mode = ResourceMode.New, Name = "cae-shop", LogsDestination = "existing-workspace", LogsWorkspace = new() { Name = "ws" }
            }));
        Assert.Empty(Errors(d => d.ContainerAppEnvironment = new()
        {
            Mode = ResourceMode.New, Name = "cae-shop", LogsDestination = "existing-workspace",
            LogsWorkspace = new() { Name = "ws", ResourceGroup = "rg-logs" }
        }));
    }

    [Fact]
    public void Pull_identity_rules()
    {
        Assert.Contains("Destination:AcrPullIdentity:Mode: only an existing identity can be used.",
            Errors(d => d.AcrPullIdentity = new() { Mode = ResourceMode.New, Name = "id" }));
        Assert.Contains("Destination:AcrPullIdentity:Name: choose an identity.",
            Errors(d => { d.ContainerRegistry = Existing("crshared01"); d.AcrPullIdentity = new() { Mode = ResourceMode.Existing }; }));
        Assert.Contains("Destination:AcrPullIdentity: an existing pull identity needs an existing registry that it can already pull from.",
            Errors(d => d.AcrPullIdentity = Existing("id-pull")));
        Assert.Empty(Errors(d => { d.ContainerRegistry = Existing("crshared01"); d.AcrPullIdentity = Existing("id-pull"); }));
    }

    [Fact]
    public void Invalid_existing_resource_group_is_refused() =>
        Assert.Contains(Errors(d => d.ContainerRegistry = Existing("crshared01", "rg-bad.")),
            e => e.StartsWith("Destination:ContainerRegistry:ResourceGroup:", StringComparison.Ordinal));

    [Theory]
    [InlineData("adsExistingRegistryName")]
    [InlineData("adsOverlayRunId")]
    [InlineData("ADSEXISTINGx")]
    public void Reserved_parameter_names_are_refused(string name)
    {
        var p = Profile();
        p.Parameters[name] = "x";
        Assert.Contains($"Parameters:{name}: names starting with adsExisting or adsOverlay are reserved by Aspire Deploy.", ProfileRules.Validate(p));
    }

    [Fact]
    public void Reserved_environment_variables_are_refused_through_a_custom_config_key()
    {
        var p = Profile();
        p.Parameters["innocent"] = "x";
        p.ParameterConfigKeys["innocent"] = "Parameters:adsExistingEnvironmentName";
        Assert.Contains(ProfileRules.Validate(p), e => e.StartsWith("ParameterConfigKeys:innocent:", StringComparison.Ordinal));

        p.ParameterConfigKeys["innocent"] = "CustomAfterMicrosoftCommonTargets";
        Assert.Contains(ProfileRules.Validate(p), e => e.StartsWith("ParameterConfigKeys:innocent:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Parameters__adsExistingRegistryName")]
    [InlineData("parameters__ADSOVERLAYRUNID")]
    [InlineData("CustomAfterMicrosoftCommonTargets")]
    [InlineData("custombeforemicrosoftcommontargets")]
    public void Overlay_variables_are_reserved(string variable) =>
        Assert.True(Core.Deploy.DeployEnvironment.IsReserved(variable));

    [Fact]
    public void Ordinary_parameters_are_not_reserved() =>
        Assert.False(Core.Deploy.DeployEnvironment.IsReserved("Parameters__adsLike"));

    // --- ApplySet ---

    [Fact]
    public void ApplySet_sets_every_new_key()
    {
        var p = Profile();
        ProfileRules.ApplySet(p, "Destination:ContainerRegistry:Mode", "existing", false);
        ProfileRules.ApplySet(p, "Destination:ContainerRegistry:Name", "crshared01", false);
        ProfileRules.ApplySet(p, "Destination:ContainerRegistry:ResourceGroup", "rg-shared", false);
        ProfileRules.ApplySet(p, "Destination:ContainerRegistry:Sku", "Standard", false);
        ProfileRules.ApplySet(p, "Destination:ContainerAppEnvironment:Mode", "NEW", false);
        ProfileRules.ApplySet(p, "Destination:ContainerAppEnvironment:Name", "cae-shop", false);
        ProfileRules.ApplySet(p, "Destination:ContainerAppEnvironment:ResourceGroup", "rg-x", false);
        ProfileRules.ApplySet(p, "Destination:ContainerAppEnvironment:LogsDestination", "existing-workspace", false);
        ProfileRules.ApplySet(p, "Destination:ContainerAppEnvironment:AppHostResource", "env", false);
        ProfileRules.ApplySet(p, "Destination:ContainerAppEnvironment:LogsWorkspace:Name", "ws", false);
        ProfileRules.ApplySet(p, "Destination:ContainerAppEnvironment:LogsWorkspace:ResourceGroup", "rg-logs", false);
        ProfileRules.ApplySet(p, "Destination:AcrPullIdentity:Mode", "Existing", false);
        ProfileRules.ApplySet(p, "Destination:AcrPullIdentity:Name", "id-pull", false);
        ProfileRules.ApplySet(p, "Destination:AcrPullIdentity:ResourceGroup", "rg-id", false);

        var d = p.Destination;
        Assert.Equal(ResourceMode.Existing, d.ContainerRegistry!.Mode);
        Assert.Equal(("crshared01", "rg-shared", "Standard"), (d.ContainerRegistry.Name, d.ContainerRegistry.ResourceGroup, d.ContainerRegistry.Sku));
        Assert.Equal(ResourceMode.New, d.ContainerAppEnvironment!.Mode);
        Assert.Equal(("cae-shop", "rg-x", "existing-workspace", "env"),
            (d.ContainerAppEnvironment.Name, d.ContainerAppEnvironment.ResourceGroup, d.ContainerAppEnvironment.LogsDestination, d.ContainerAppEnvironment.AppHostResource));
        Assert.Equal(("ws", "rg-logs"), (d.ContainerAppEnvironment.LogsWorkspace!.Name, d.ContainerAppEnvironment.LogsWorkspace.ResourceGroup));
        Assert.Equal((ResourceMode.Existing, "id-pull", "rg-id"), (d.AcrPullIdentity!.Mode!.Value, d.AcrPullIdentity.Name, d.AcrPullIdentity.ResourceGroup));
    }

    [Fact]
    public void ApplySet_empty_value_clears_and_AspireDefault_stores_nothing()
    {
        var p = Profile(d => d.ContainerRegistry = Existing("crshared01"));
        ProfileRules.ApplySet(p, "Destination:ContainerRegistry:Name", "", false);
        Assert.Null(p.Destination.ContainerRegistry!.Name);
        ProfileRules.ApplySet(p, "Destination:ContainerRegistry:Mode", "AspireDefault", false);
        Assert.Null(p.Destination.ContainerRegistry.Mode);
    }

    [Theory]
    [InlineData("Maybe")]
    [InlineData("1")]
    public void ApplySet_refuses_unknown_modes(string value)
    {
        var ex = Assert.Throws<DeployerException>(() => ProfileRules.ApplySet(Profile(), "Destination:ContainerRegistry:Mode", value, false));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
    }

    // --- ApplyTo (D15) ---

    [Fact]
    public void ApplyTo_copies_only_changed_resource_fields()
    {
        var original = Profile(d => d.ContainerRegistry = new() { Mode = ResourceMode.New, Name = "crshop01", Sku = "Basic" });
        var session = new ProfileEditSession(original);
        session.Edited.Destination.ContainerAppEnvironment = new() { Mode = ResourceMode.Existing, Name = "cae-shared", ResourceGroup = "rg-shared" };

        // Meanwhile a deploy switched the registry to Existing (D15).
        var stored = original.Clone();
        stored.Destination.ContainerRegistry = new() { Mode = ResourceMode.Existing, Name = "crshop01", ResourceGroup = "rg-shop" };
        session.ApplyTo(stored);

        Assert.Equal(ResourceMode.Existing, stored.Destination.ContainerRegistry!.Mode);
        Assert.Equal("rg-shop", stored.Destination.ContainerRegistry.ResourceGroup);
        Assert.Null(stored.Destination.ContainerRegistry.Sku);
        Assert.Equal("cae-shared", stored.Destination.ContainerAppEnvironment!.Name);
        Assert.Equal(ResourceMode.Existing, stored.Destination.ContainerAppEnvironment.Mode);
    }

    [Fact]
    public void ApplyTo_back_to_AspireDefault_removes_the_section()
    {
        var original = Profile(d => d.ContainerRegistry = Existing("crshared01"));
        var session = new ProfileEditSession(original);
        session.Edited.Destination.ContainerRegistry = null;
        var stored = original.Clone();
        session.ApplyTo(stored);
        Assert.Null(stored.Destination.ContainerRegistry);
    }

    [Fact]
    public void ApplyTo_without_changes_leaves_an_AspireDefault_profile_without_sections()
    {
        var original = Profile();
        var stored = original.Clone();
        new ProfileEditSession(original).ApplyTo(stored);
        Assert.Null(stored.Destination.ContainerRegistry);
        Assert.Null(stored.Destination.ContainerAppEnvironment);
        Assert.Null(stored.Destination.AcrPullIdentity);
    }

    [Fact]
    public async Task Profile_summary_names_chosen_registry_and_environment()
    {
        var path = _dir.Combine("s.json");
        var service = new ProfileService(new ProfileStore(new FixedLocator(path)));
        await service.SaveAsync("shop", Profile(d =>
        {
            d.ContainerRegistry = Existing("crshared01");
            d.ContainerAppEnvironment = new() { Name = "cae-ignored" }; // no mode = AspireDefault
        }), overwrite: true, CancellationToken.None);
        var summary = Assert.Single(await service.ListAsync(CancellationToken.None));
        Assert.Equal("crshared01", summary.RegistryName);
        Assert.Null(summary.EnvironmentName);
    }
}
