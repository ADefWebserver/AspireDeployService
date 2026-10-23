using System.Text.Json;
using AspireDeployService.Core.Deploy;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Services;
using AspireDeployService.Core.Settings;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests.Deploy;

/// <summary>Resource pickers plan §7.2 and §11: overlay, plan, preview, creation and the marker check inside a deploy.</summary>
[Collection("AppHostLock")]
public class DeployServiceResourceTests : IDisposable
{
    private const string Sub = "00000000-0000-0000-0000-000000000001";
    private const string SharedEnvId = $"/subscriptions/{Sub}/resourceGroups/rg-shared/providers/Microsoft.App/managedEnvironments/cae-shared";

    private readonly Harness _h = new();
    private readonly List<(string Prefix, ProcessResult Result)> _az = [];

    public DeployServiceResourceTests()
    {
        _h.OnAz = spec =>
        {
            var args = string.Join(' ', spec.Arguments);
            return _az.FirstOrDefault(r => args.StartsWith(r.Prefix, StringComparison.Ordinal)).Result;
        };
        // The overlay writes its marker file when the AppHost runs in publish mode; the fake deploy does the same.
        _h.OnDeploy = (spec, _) =>
        {
            if (spec.Environment.TryGetValue("Parameters__adsOverlayMarkerFile", out var marker) && marker is not null)
                File.WriteAllText(marker, AppHostOverlay.MarkerPrefix + spec.Environment["Parameters__adsOverlayRunId"]);
            return Task.FromResult(FakeProcessRunner.Ok("Deployment completed."));
        };
    }

    public void Dispose() => _h.Dispose();

    private void On(string prefix, string json) => _az.Add((prefix, FakeProcessRunner.Ok(json)));
    private void On(string prefix, ProcessResult result) => _az.Add((prefix, result));

    private List<string> AzCalls => _h.AzSpecs.Select(s => string.Join(' ', s.Arguments)).ToList();

    private Task<OperationResult> DeployAsync(bool dryRun = false) =>
        _h.Deploy().DeployAsync(new DeployRequest("shop", null, dryRun, ClearCache: false), _h.Observer, CancellationToken.None);

    private static ResourceChoice Existing(string name) => new() { Mode = ResourceMode.Existing, Name = name, ResourceGroup = "rg-shared" };

    private Task ExistingProfileAsync() => _h.AddProfileAsync(change: p =>
    {
        p.Destination.ContainerRegistry = Existing("crshared01");
        p.Destination.ContainerAppEnvironment = Existing("cae-shared");
    });

    private void ExistingInAzure()
    {
        On("acr show", File.ReadAllText(AppHostFixtures.Fixture("az", "acr-show.json")));
        On("containerapp env show", File.ReadAllText(AppHostFixtures.Fixture("az", "containerapp-env-show.json")));
        On("containerapp list", "[]");
    }

    [Fact]
    public async Task All_AspireDefault_deploys_with_todays_environment_and_no_az_writes()
    {
        await _h.AddProfileAsync();

        var result = await DeployAsync();

        Assert.Equal(OutcomeCode.Success, result.Code);
        var spec = Assert.Single(_h.DeploySpecs);
        Assert.Equal(
            new[]
            {
                "ASPIRE_CLI_TELEMETRY_OPTOUT", "Azure__AllowResourceGroupCreation", "Azure__CredentialSource", "Azure__Location",
                "Azure__ResourceGroup", "Azure__SubscriptionId", "ConnectionStrings__db", "DOTNET_ENVIRONMENT", "PATH",
                "Parameters__apiKey", "Parameters__region_name"
            },
            spec.Environment.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Shop.AppHost/Shop.AppHost.csproj", spec.Arguments[spec.Arguments.ToList().IndexOf("--apphost") + 1]);
        Assert.All(AzCalls, c => Assert.StartsWith("containerapp list", c)); // the preview only reads
        Assert.False(Directory.Exists(Path.Combine(_h.WorkRoot, "shop", "overlay")));
    }

    [Fact]
    public async Task Existing_resources_reach_the_spec_through_the_overlay()
    {
        await ExistingProfileAsync();
        ExistingInAzure();

        var result = await DeployAsync();

        Assert.Equal(OutcomeCode.Success, result.Code);
        var env = Assert.Single(_h.DeploySpecs).Environment;
        Assert.Equal(Path.Combine(_h.WorkRoot, "shop", "overlay", "ads.overlay.targets"), env["CustomAfterMicrosoftCommonTargets"]);
        Assert.Equal("cae-shared", env["Parameters__adsExistingEnvironmentName"]);
        Assert.Equal("rg-shared", env["Parameters__adsExistingEnvironmentResourceGroup"]);
        Assert.Equal("crshared01", env["Parameters__adsExistingRegistryName"]);
        Assert.Equal("rg-shared", env["Parameters__adsExistingRegistryResourceGroup"]);
        Assert.False(env.ContainsKey("Parameters__adsExistingPullIdentityName"));
        Assert.DoesNotContain(AzCalls, c => c.Contains(" create ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirmation_lists_the_resources_and_the_container_apps()
    {
        await ExistingProfileAsync();
        ExistingInAzure();

        await DeployAsync();

        var (_, message) = Assert.Single(_h.Interaction.Asked);
        Assert.Contains("  registry: crshared01 in rg-shared (existing)\n", message);
        Assert.Contains("  apps environment: cae-shared in rg-shared (existing)\n", message);
        Assert.Contains("  container apps: apiservice (new), cache (new), webfrontend (new)", message);
        Assert.Contains("  resource group: rg-shop in eastus2\n", message);
    }

    [Fact]
    public async Task Exit_0_without_the_marker_file_fails_the_deploy()
    {
        await ExistingProfileAsync();
        ExistingInAzure();
        _h.OnDeploy = (_, _) => Task.FromResult(FakeProcessRunner.Ok("Deployment completed."));

        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());

        Assert.Equal(OutcomeCode.DeployFailed, ex.Code);
        Assert.Equal("The AppHost overlay was not applied, so Aspire may have created a new environment or registry in rg-shop. See Troubleshooting.", ex.Message);
    }

    [Fact]
    public async Task Without_an_overlay_there_is_no_marker_check()
    {
        await _h.AddProfileAsync();
        _h.OnDeploy = (_, _) => Task.FromResult(FakeProcessRunner.Ok("Deployment completed."));
        Assert.Equal(OutcomeCode.Success, (await DeployAsync()).Code);
    }

    [Fact]
    public async Task Dry_run_with_new_resources_creates_nothing_and_lists_steps_without_the_overlay()
    {
        await _h.AddProfileAsync(change: p =>
        {
            p.Destination.ContainerRegistry = new() { Mode = ResourceMode.New, Name = "crnew01" };
            p.Destination.ContainerAppEnvironment = new() { Mode = ResourceMode.New, Name = "cae-new" };
        });
        On("group exists", "true");
        On("acr show", FakeProcessRunner.Fail(3, "ERROR: (ResourceNotFound) The Resource was not found."));
        On("acr check-name", File.ReadAllText(AppHostFixtures.Fixture("az", "acr-check-name-free.json")));
        On("containerapp env show", FakeProcessRunner.Fail(3, "ERROR: (ResourceNotFound) The Resource was not found."));
        On("containerapp list", "[]");

        var result = await DeployAsync(dryRun: true);

        Assert.Equal(OutcomeCode.Success, result.Code);
        Assert.DoesNotContain(AzCalls, c => c.Contains(" create ", StringComparison.Ordinal));
        var spec = Assert.Single(_h.DeploySpecs);
        Assert.Contains("--list-steps", spec.Arguments);
        Assert.False(spec.Environment.ContainsKey("CustomAfterMicrosoftCommonTargets"));
        Assert.True(_h.Observer.Contains("Listing steps without the overlay because crnew01 does not exist yet."));
        Assert.Equal(ResourceMode.New, (await _h.Profiles.GetAsync("shop", CancellationToken.None))!.Destination.ContainerRegistry!.Mode);
    }

    [Fact]
    public async Task Dry_run_with_only_existing_resources_lists_steps_with_the_overlay()
    {
        await ExistingProfileAsync();
        ExistingInAzure();
        await DeployAsync(dryRun: true);
        Assert.True(Assert.Single(_h.DeploySpecs).Environment.ContainsKey("CustomAfterMicrosoftCommonTargets"));
    }

    [Fact]
    public async Task Creation_runs_after_the_confirmation_and_the_profile_switches()
    {
        await _h.AddProfileAsync(change: p =>
        {
            p.Destination.ContainerAppEnvironment = new() { Mode = ResourceMode.New, Name = "cae-new" };
        });
        On("group exists", "true");
        On("containerapp env show", FakeProcessRunner.Fail(3, "ERROR: (ResourceNotFound) The Resource was not found."));
        On("containerapp env create", JsonSerializer.Serialize(new { id = $"/subscriptions/{Sub}/resourceGroups/rg-shop/providers/Microsoft.App/managedEnvironments/cae-new" }));
        On("containerapp list", "[]");

        var result = await DeployAsync();

        Assert.Equal(OutcomeCode.Success, result.Code);
        var order = _h.Runner.Specs.Select(s => string.Join(' ', s.Arguments)).ToList();
        Assert.True(order.FindIndex(c => c.StartsWith("containerapp env create", StringComparison.Ordinal))
            < order.FindIndex(c => c.StartsWith("deploy ", StringComparison.Ordinal)));
        var (_, message) = Assert.Single(_h.Interaction.Asked);
        Assert.Contains("apps environment: cae-new in rg-shop (create, logs: log-analytics)", message);
        Assert.Contains("Creating the environment can take 30 minutes or more.", message);
        Assert.Equal(ResourceMode.Existing, (await _h.Profiles.GetAsync("shop", CancellationToken.None))!.Destination.ContainerAppEnvironment!.Mode);
    }

    [Fact]
    public async Task Declined_confirmation_creates_nothing()
    {
        _h.Interaction = new FakeUserInteraction(answer: false);
        await _h.AddProfileAsync(change: p => p.Destination.ContainerAppEnvironment = new() { Mode = ResourceMode.New, Name = "cae-new" });
        On("group exists", "true");
        On("containerapp env show", FakeProcessRunner.Fail(3, "ERROR: (ResourceNotFound) The Resource was not found."));
        On("containerapp list", "[]");

        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());

        Assert.Equal(OutcomeCode.Declined, ex.Code);
        Assert.DoesNotContain(AzCalls, c => c.Contains(" create ", StringComparison.Ordinal));
        Assert.Empty(_h.DeploySpecs);
    }

    [Fact]
    public async Task Conflict_rows_block_before_the_confirmation()
    {
        await ExistingProfileAsync();
        ExistingInAzure();
        _az.RemoveAll(a => a.Prefix == "containerapp list");
        On("containerapp list --subscription " + Sub + " --resource-group", "[]");
        On("containerapp list --subscription " + Sub + " --output",
            JsonSerializer.Serialize(new[] { new { name = "webfrontend", resourceGroup = "rg-team2", properties = new { managedEnvironmentId = SharedEnvId } } }));

        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());

        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Contains("webfrontend: webfrontend in rg-team2 already uses this name in the shared environment.", ex.Message);
        Assert.Empty(_h.Interaction.Asked);
        Assert.Empty(_h.DeploySpecs);
    }

    [Fact]
    public async Task Missing_existing_resource_stops_before_the_confirmation()
    {
        await ExistingProfileAsync();
        On("acr show", FakeProcessRunner.Fail(3, "ERROR: (ResourceNotFound) The Resource was not found."));

        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());

        Assert.StartsWith("Container registry crshared01 was not found in rg-shared.", ex.Message);
        Assert.Empty(_h.Interaction.Asked);
    }

    [Fact]
    public async Task Another_run_building_the_same_AppHost_times_out_with_the_message()
    {
        await _h.AddProfileAsync();
        var appHost = Path.Combine(_h.SourceFolder, "Shop.AppHost", "Shop.AppHost.csproj");
        var before = DiscoveryPipeline.AppHostLockTimeout;
        DiscoveryPipeline.AppHostLockTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            await using var held = await FileLock.AcquireAsync(DiscoveryPipeline.AppHostLockPath(_h.WorkRoot, appHost), TimeSpan.FromSeconds(1), CancellationToken.None);
            var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());
            Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
            Assert.Equal("Another run is building this AppHost. Wait for it to finish.", ex.Message);
        }
        finally
        {
            DiscoveryPipeline.AppHostLockTimeout = before;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task File_based_AppHost_deploys_from_a_sibling_copy_that_is_always_removed(bool succeed)
    {
        _h.Dir.Write("src/apphost.cs", "#:sdk Aspire.AppHost.Sdk@13.6.1\n#:package Aspire.Hosting.Azure.AppContainers@13.6.1\n"
            + "var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddAzureContainerAppEnvironment(\"env\");\nbuilder.Build().Run();\n");
        await _h.AddProfileAsync(change: p =>
        {
            p.Source.AppHostPath = "apphost.cs";
            p.Destination.ContainerRegistry = Existing("crshared01");
            p.Destination.ContainerAppEnvironment = Existing("cae-shared");
        });
        ExistingInAzure();
        var sibling = Path.Combine(_h.SourceFolder, ".ads-deploy", "apphost.cs");
        var seen = false;
        var inner = _h.OnDeploy;
        _h.OnDeploy = async (spec, ct) =>
        {
            seen = File.Exists(sibling) && SiblingOverlay.HasMarker(sibling);
            Assert.Equal(".ads-deploy/apphost.cs", spec.Arguments[spec.Arguments.ToList().IndexOf("--apphost") + 1]);
            Assert.False(spec.Environment.ContainsKey("CustomAfterMicrosoftCommonTargets"));
            return succeed ? await inner(spec, ct) : FakeProcessRunner.Fail(1, "boom");
        };

        if (succeed)
            Assert.Equal(OutcomeCode.Success, (await DeployAsync()).Code);
        else
            Assert.Equal(OutcomeCode.DeployFailed, (await Assert.ThrowsAsync<DeployerException>(() => DeployAsync())).Code);

        Assert.True(seen);
        Assert.False(Directory.Exists(Path.GetDirectoryName(sibling)));
        Assert.NotEmpty(File.ReadAllText(Path.Combine(_h.SourceFolder, "apphost.cs")));
    }

    [Fact]
    public async Task A_leftover_sibling_folder_is_removed_at_the_start_of_a_deploy()
    {
        var leftover = _h.Dir.Write("src/Shop.AppHost/.ads-deploy/apphost.cs", SiblingOverlay.Marker + "\n");
        await _h.AddProfileAsync();
        await DeployAsync();
        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public void Lock_path_is_stable_and_case_insensitive()
    {
        var a = DiscoveryPipeline.AppHostLockPath(@"C:\w", @"C:\Src\Shop.AppHost\Shop.AppHost.csproj");
        var b = DiscoveryPipeline.AppHostLockPath(@"C:\w", @"c:\src\shop.apphost\shop.apphost.csproj");
        Assert.Equal(a, b);
        Assert.Matches(@"^C:\\w\\locks\\apphost-[0-9a-f]{12}\.lock$", a);
    }

    [Fact]
    public async Task Overlay_choices_on_an_AppHost_without_Container_Apps_are_refused()
    {
        AppHostFixtures.ProjectAppHost(_h.Dir, "src/Shop.AppHost", "13.6.1",
            "var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddAzureAppServiceEnvironment(\"env\");\nbuilder.Build().Run();");
        File.WriteAllText(Path.Combine(_h.SourceFolder, "Shop.AppHost", "Shop.AppHost.csproj"), """
            <Project Sdk="Aspire.AppHost.Sdk/13.6.1"><ItemGroup><PackageReference Include="Aspire.Hosting.Azure.AppService" Version="13.6.1" /></ItemGroup></Project>
            """);
        await ExistingProfileAsync();

        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());

        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Contains("apply to Azure Container Apps only", ex.Message);
    }

    [Fact]
    public async Task Adopted_profile_whose_group_is_gone_never_starts_aspire_deploy()
    {
        // Wizard plan T0: aspire deploy 13.6.1 created rg-azinv-gone although AllowResourceGroupCreation was false.
        await _h.AddProfileAsync(change: p => p.Destination.AllowResourceGroupCreation = false);
        On("group exists", "false");
        On("containerapp list", "[]");

        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());

        Assert.Equal("Resource group rg-shop does not exist and the profile does not allow creating it.", ex.Message);
        Assert.Empty(_h.DeploySpecs);
        Assert.Empty(_h.Interaction.Asked);
    }
}

[CollectionDefinition("AppHostLock", DisableParallelization = true)]
public sealed class AppHostLockCollection;
