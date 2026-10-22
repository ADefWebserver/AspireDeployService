using AspireDeployService.Core.Azure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests.Azure;

/// <summary>Resource pickers plan §6.3: the new lists, parsed from captured az output (T0 fixtures).</summary>
public class AzureAccountListTests : IDisposable
{
    private const string Sub = "00000000-0000-0000-0000-000000000001";
    private const string Other = "00000000-0000-0000-0000-000000000002";

    private readonly TempDir _dir = new();
    private readonly List<ProcessSpec> _calls = [];
    private readonly AzureAccountService _service;

    public Func<string, ProcessResult?> Override { get; set; } = _ => null;

    public static string Az(string name) => File.ReadAllText(AppHostFixtures.Fixture("az", name));

    public AzureAccountListTests()
    {
        var path = _dir.Write("appsettings.Local.json", $$"""{ "AspireDeploy": { "WorkRoot": "{{_dir.Combine("w").Replace("\\", "/")}}" } }""");
        var runner = new FakeProcessRunner((spec, _) =>
        {
            lock (_calls)
                _calls.Add(spec);
            var args = string.Join(' ', spec.Arguments);
            if (Override(args) is { } overridden)
                return Task.FromResult(overridden);
            return Task.FromResult(args switch
            {
                _ when args.StartsWith("acr list ") => FakeProcessRunner.Ok(Az("acr-list.json")),
                _ when args.StartsWith("acr check-name --name crshared01 ") => FakeProcessRunner.Ok(Az("acr-check-name-taken.json")),
                _ when args.StartsWith("acr check-name ") => FakeProcessRunner.Ok(Az("acr-check-name-free.json")),
                _ when args.StartsWith("containerapp env list ") => FakeProcessRunner.Ok(Az("containerapp-env-list.json")),
                _ when args.StartsWith("containerapp list ") && args.Contains("--resource-group nope") =>
                    FakeProcessRunner.Fail(1, Az("app-list-norg.txt")),
                _ when args.StartsWith("containerapp list ") => FakeProcessRunner.Ok(Az("containerapp-list.json")),
                _ when args.StartsWith("identity list ") => FakeProcessRunner.Ok(Az("identity-list.json")),
                _ when args.StartsWith("monitor log-analytics workspace list ") => FakeProcessRunner.Ok(Az("workspace-list.json")),
                _ => FakeProcessRunner.Fail(1, "ERROR: unexpected")
            });
        });
        _service = new AzureAccountService(new ProfileStore(new FixedLocator(path)), new FakeRunContextFactory(runner), TimeProvider.System);
    }

    public void Dispose() => _dir.Dispose();

    private int Count(string prefix)
    {
        lock (_calls)
            return _calls.Count(c => string.Join(' ', c.Arguments).StartsWith(prefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registries_parse_the_captured_fixture()
    {
        var r = Assert.Single(await _service.ListContainerRegistriesAsync(Sub, NullRunObserver.Instance, CancellationToken.None));
        Assert.Equal(new AzureContainerRegistry("crshared01", "rg-shared", "eastus2", "crshared01.azurecr.io", "Basic"), r);
        var spec = _calls.Single();
        Assert.Equal(["acr", "list", "--subscription", Sub, "--output", "json"], spec.Arguments);
        Assert.False(spec.LogStdOut);
    }

    [Fact]
    public async Task Environments_are_sorted_by_name()
    {
        var list = await _service.ListContainerAppEnvironmentsAsync(Sub, NullRunObserver.Instance, CancellationToken.None);
        Assert.Equal(["aaa-env", "cae-shared"], list.Select(e => e.Name));
        Assert.EndsWith("/managedEnvironments/cae-shared", list[1].Id);
        Assert.Equal("East US 2", list[1].Location);
    }

    [Fact]
    public async Task Container_apps_read_both_environment_id_fields()
    {
        var list = await _service.ListContainerAppsAsync(Sub, null, NullRunObserver.Instance, CancellationToken.None);
        Assert.Equal(["apiservice", "webfrontend"], list.Select(a => a.Name));
        Assert.All(list, a => Assert.EndsWith("/managedEnvironments/cae-shared", a.EnvironmentId));
    }

    [Fact]
    public async Task Container_apps_in_a_missing_group_are_an_empty_list() =>
        Assert.Empty(await _service.ListContainerAppsAsync(Sub, "nope", NullRunObserver.Instance, CancellationToken.None));

    [Fact]
    public async Task Container_apps_refuse_an_invalid_group() =>
        Assert.Equal(OutcomeCode.InvalidInput, (await Assert.ThrowsAsync<DeployerException>(() =>
            _service.ListContainerAppsAsync(Sub, "bad.", NullRunObserver.Instance, CancellationToken.None))).Code);

    [Fact]
    public async Task Identities_and_workspaces_parse()
    {
        var id = Assert.Single(await _service.ListManagedIdentitiesAsync(Sub, NullRunObserver.Instance, CancellationToken.None));
        Assert.Equal(("id-pull", "rg-shared", "99999999-0000-0000-0000-000000000009"), (id.Name, id.ResourceGroup, id.PrincipalId));
        var ws = Assert.Single(await _service.ListLogAnalyticsWorkspacesAsync(Sub, NullRunObserver.Instance, CancellationToken.None));
        Assert.Equal(("ws-shared", "rg-shared", "eastus2"), (ws.Name, ws.ResourceGroup, ws.Location));
        Assert.EndsWith("/workspaces/ws-shared", ws.Id);
    }

    [Fact]
    public async Task Registry_name_check_is_never_cached()
    {
        var taken = await _service.CheckRegistryNameAsync(Sub, "crshared01", NullRunObserver.Instance, CancellationToken.None);
        await _service.CheckRegistryNameAsync(Sub, "crshared01", NullRunObserver.Instance, CancellationToken.None);
        Assert.False(taken.Available);
        Assert.Equal("The registry crshared01 is already in use.", taken.Reason);
        Assert.Equal(2, Count("acr check-name"));
        var free = await _service.CheckRegistryNameAsync(Sub, "crfreename01", NullRunObserver.Instance, CancellationToken.None);
        Assert.Equal(new NameAvailability(true, null), free);
    }

    [Fact]
    public void Name_check_shapes_parse()
    {
        Assert.Equal("Resource names may contain alpha numeric characters only and must be between 5 and 50 characters.",
            AzureAccountService.ParseNameAvailability(Az("acr-check-name-invalid.json")).Reason);
    }

    [Fact]
    public async Task Lists_are_cached_and_refresh_reads_again()
    {
        await _service.ListContainerRegistriesAsync(Sub, NullRunObserver.Instance, CancellationToken.None);
        await _service.ListContainerRegistriesAsync(Sub, NullRunObserver.Instance, CancellationToken.None);
        Assert.Equal(1, Count("acr list"));
        await _service.ListContainerRegistriesAsync(Sub, NullRunObserver.Instance, CancellationToken.None, refresh: true);
        Assert.Equal(2, Count("acr list"));
    }

    [Fact]
    public async Task Invalidate_drops_only_that_subscription()
    {
        await _service.ListContainerRegistriesAsync(Sub, NullRunObserver.Instance, CancellationToken.None);
        await _service.ListContainerAppsAsync(Sub, "rg-shop", NullRunObserver.Instance, CancellationToken.None);
        await _service.ListContainerRegistriesAsync(Other, NullRunObserver.Instance, CancellationToken.None);

        _service.Invalidate(Sub);

        await _service.ListContainerRegistriesAsync(Sub, NullRunObserver.Instance, CancellationToken.None);
        await _service.ListContainerAppsAsync(Sub, "rg-shop", NullRunObserver.Instance, CancellationToken.None);
        await _service.ListContainerRegistriesAsync(Other, NullRunObserver.Instance, CancellationToken.None);
        Assert.Equal(3, Count("acr list"));         // Sub twice, Other once
        Assert.Equal(2, Count("containerapp list"));
    }

    [Fact]
    public async Task Unregistered_provider_gives_the_fix_line_and_the_full_command()
    {
        Override = a => a.StartsWith("containerapp env list") ? FakeProcessRunner.Fail(1,
            "ERROR: (MissingSubscriptionRegistration) The subscription is not registered to use namespace 'Microsoft.App'.") : null;
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _service.ListContainerAppEnvironmentsAsync(Sub, NullRunObserver.Instance, CancellationToken.None));
        Assert.StartsWith("az containerapp env list failed: ERROR: (MissingSubscriptionRegistration)", ex.Message);
        Assert.Contains("Run: az provider register --namespace Microsoft.App", ex.Message);
    }

    [Fact]
    public async Task Non_guid_subscription_is_refused() =>
        Assert.Equal(OutcomeCode.InvalidInput, (await Assert.ThrowsAsync<DeployerException>(() =>
            _service.ListContainerRegistriesAsync("nope", NullRunObserver.Instance, CancellationToken.None))).Code);
}

public class AzCliTests
{
    [Theory]
    [InlineData("acr-show-notfound.txt", AzErrorKind.NotFound)]
    [InlineData("env-show-notfound.txt", AzErrorKind.NotFound)]
    [InlineData("app-list-norg.txt", AzErrorKind.ResourceGroupNotFound)]
    public void Classifies_captured_errors(string fixture, AzErrorKind kind) =>
        Assert.Equal(kind, AzCli.Classify(AzCli.ErrorLine(AzureAccountListTests.Az(fixture))));

    [Theory]
    [InlineData("ERROR: (AuthorizationFailed) The client 'x' does not have authorization to perform action", AzErrorKind.Unauthorized)]
    [InlineData("ERROR: (MissingSubscriptionRegistration) The subscription is not registered", AzErrorKind.ProviderNotRegistered)]
    [InlineData("ERROR: (Conflict) Another operation is in progress", AzErrorKind.Conflict)]
    [InlineData("ERROR: The registry crx is already in use.", AzErrorKind.Conflict)]
    [InlineData("ERROR: Please run 'az login' to setup account.", AzErrorKind.NotSignedIn)]
    [InlineData("ERROR: something odd", AzErrorKind.Other)]
    [InlineData("", AzErrorKind.Other)]
    public void Classifies_error_lines(string line, AzErrorKind kind) => Assert.Equal(kind, AzCli.Classify(line));

    [Fact]
    public void Error_line_is_the_first_ERROR_line() =>
        Assert.Equal("ERROR: boom", AzCli.ErrorLine("WARNING: x\nERROR: boom\nCode: y\n"));

    [Fact]
    public void Describe_stops_at_the_first_option() =>
        Assert.Equal("az containerapp env list", AzCli.Describe(["containerapp", "env", "list", "--subscription", "s"]));
}
