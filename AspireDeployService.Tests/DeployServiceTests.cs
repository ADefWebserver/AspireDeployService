using AspireDeployService.Core.Errors;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Services;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests;

public class DeployServiceTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private Task<OperationResult> DeployAsync(bool dryRun = false, CancellationToken ct = default) =>
        _h.Deploy().DeployAsync(new DeployRequest("shop", null, dryRun, ClearCache: false), _h.Observer, ct);

    [Fact]
    public async Task Success_passes_values_only_through_the_environment()
    {
        await _h.AddProfileAsync(change: p => p.Destination.TenantId = "00000000-0000-0000-0000-0000000000aa");

        var result = await DeployAsync();

        Assert.Equal(OutcomeCode.Success, result.Code);
        var spec = Assert.Single(_h.DeploySpecs);
        Assert.Contains("--non-interactive", spec.Arguments);
        Assert.Equal("Production", spec.Arguments[spec.Arguments.ToList().IndexOf("-e") + 1]);
        var env = spec.Environment;
        Assert.Equal("00000000-0000-0000-0000-000000000001", env["Azure__SubscriptionId"]);
        Assert.Equal("eastus2", env["Azure__Location"]);
        Assert.Equal("rg-shop", env["Azure__ResourceGroup"]);
        Assert.Equal("00000000-0000-0000-0000-0000000000aa", env["Azure__TenantId"]);
        Assert.Equal("AzureCli", env["Azure__CredentialSource"]);
        Assert.Equal("true", env["Azure__AllowResourceGroupCreation"]);
        Assert.Equal("abc12", env["Parameters__apiKey"]);
        Assert.Equal("west", env["Parameters__region_name"]);
        Assert.Equal("Server=db;Password=hunter2-long", env["ConnectionStrings__db"]);
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("abc12") || a.Contains("hunter2"));
    }

    [Fact]
    public async Task Secrets_of_any_length_never_reach_the_log()
    {
        await _h.AddProfileAsync();
        _h.OnDeploy = (spec, _) => Task.FromResult(FakeProcessRunner.Ok($"key={spec.Environment["Parameters__apiKey"]} db={spec.Environment["ConnectionStrings__db"]}"));

        await DeployAsync();

        Assert.False(_h.Observer.Contains("abc12"));
        Assert.False(_h.Observer.Contains("hunter2-long"));
        Assert.True(_h.Observer.Contains("key=[REDACTED]"));
        var log = File.ReadAllText(_h.Observer.LogFile!);
        Assert.DoesNotContain("abc12", log);
        var history = File.ReadAllText(Path.Combine(_h.WorkRoot, "shop", "history.jsonl"));
        Assert.DoesNotContain("abc12", history);
        Assert.Contains("\"exitCode\":0", history);
    }

    [Fact]
    public async Task Missing_required_parameter_is_code_2_before_deploy()
    {
        await _h.AddProfileAsync(change: p => p.Parameters.Remove("apiKey"));
        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Contains("apiKey", ex.Message);
        Assert.Empty(_h.DeploySpecs);
    }

    [Fact]
    public async Task Empty_value_means_the_apphost_supplies_it()
    {
        await _h.AddProfileAsync(change: p => p.Parameters["apiKey"] = "");
        await DeployAsync();
        Assert.False(Assert.Single(_h.DeploySpecs).Environment.ContainsKey("Parameters__apiKey"));
    }

    [Fact]
    public async Task Declined_confirmation_is_code_7_and_runs_nothing()
    {
        await _h.AddProfileAsync();
        _h.Interaction = new FakeUserInteraction(answer: false);
        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());
        Assert.Equal(OutcomeCode.Declined, ex.Code);
        Assert.Empty(_h.DeploySpecs);
        Assert.True(_h.WorkKeyIsFree("shop"));
    }

    [Fact]
    public async Task Failed_deploy_is_classified()
    {
        await _h.AddProfileAsync();
        _h.OnDeploy = (_, _) => Task.FromResult(FakeProcessRunner.Fail(1, "ERROR: QuotaExceeded: not enough cores"));
        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());
        Assert.Equal(OutcomeCode.DeployFailed, ex.Code);
        Assert.Equal(ErrorCategory.Quota, ex.Category);
        Assert.Null((await _h.Profiles.GetAsync("shop", CancellationToken.None))!.LastDeployedAspireVersion);
    }

    [Fact]
    public async Task Timeout_is_code_5_Timeout()
    {
        await _h.AddProfileAsync();
        _h.OnDeploy = (_, _) => throw new TimeoutException("no result");
        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());
        Assert.Equal(ErrorCategory.Timeout, ex.Category);
    }

    [Fact]
    public async Task Cancel_during_deploy_is_code_5_Canceled_and_releases_the_lock()
    {
        await _h.AddProfileAsync();
        using var cts = new CancellationTokenSource();
        _h.OnDeploy = async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeProcessRunner.Ok();
        };

        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync(ct: cts.Token));
        Assert.Equal(OutcomeCode.DeployFailed, ex.Code);
        Assert.Equal(ErrorCategory.Canceled, ex.Category);
        Assert.Contains("rg-shop", ex.Message);
        Assert.True(_h.WorkKeyIsFree("shop"));
    }

    [Fact]
    public async Task Cancel_while_the_confirmation_is_open_releases_the_lock()
    {
        await _h.AddProfileAsync();
        _h.Interaction = new FakeUserInteraction { WaitForCancel = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeployAsync(ct: cts.Token));
        Assert.Empty(_h.DeploySpecs);
        Assert.True(_h.WorkKeyIsFree("shop"));
    }

    [Fact]
    public async Task Second_run_on_the_same_profile_is_code_2()
    {
        await _h.AddProfileAsync();
        await using var held = WorkKeyLock.Acquire(WorkKeys.For(_h.WorkRoot, "shop", null), "shop");
        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
    }

    [Fact]
    public async Task No_azure_target_is_code_4()
    {
        await _h.AddProfileAsync();
        File.WriteAllText(_h.Dir.Combine("src", "Shop.AppHost", "AppHost.cs"), "var b = DistributedApplication.CreateBuilder(args);");
        File.WriteAllText(_h.Dir.Combine("src", "Shop.AppHost", "Shop.AppHost.csproj"), """<Project Sdk="Aspire.AppHost.Sdk/13.6.1" />""");
        var ex = await Assert.ThrowsAsync<DeployerException>(() => DeployAsync());
        Assert.Equal(OutcomeCode.DiscoveryFailed, ex.Code);
        Assert.Contains("AddAzureContainerAppEnvironment", ex.Message);
    }

    [Fact]
    public async Task Dry_run_lists_steps_and_records_no_version()
    {
        await _h.AddProfileAsync();
        await DeployAsync(dryRun: true);
        Assert.Contains("--list-steps", Assert.Single(_h.DeploySpecs).Arguments);
        Assert.Null((await _h.Profiles.GetAsync("shop", CancellationToken.None))!.LastDeployedAspireVersion);
    }

    [Fact]
    public async Task D18_success_records_the_version_and_a_change_is_warned()
    {
        await _h.AddProfileAsync(change: p => p.LastDeployedAspireVersion = "13.5.0");

        await DeployAsync();

        var (_, message) = Assert.Single(_h.Interaction.Asked);
        Assert.Contains("changed from 13.5.0 to 13.6.1", message);
        Assert.True(_h.Observer.Contains("changed from 13.5.0 to 13.6.1"));
        Assert.Equal("13.6.1", (await _h.Profiles.GetAsync("shop", CancellationToken.None))!.LastDeployedAspireVersion);
    }

    [Fact]
    public async Task D18_unchanged_version_adds_nothing()
    {
        await _h.AddProfileAsync(change: p => p.LastDeployedAspireVersion = "13.6.1");
        await DeployAsync();
        Assert.DoesNotContain("changed from", Assert.Single(_h.Interaction.Asked).Message);
    }

    [Fact]
    public async Task Mismatched_cli_on_path_is_installed_pinned()
    {
        await _h.AddProfileAsync();
        _h.CliVersion = "13.5.0";
        await DeployAsync();
        var install = Assert.Single(_h.Runner.Specs, s => FakeProcessRunner.Is(s, "tool", "install"));
        Assert.Equal(["tool", "install", "aspire.cli", "--version", "13.6.1", "--tool-path", Path.Combine(_h.Dir.Combine("tools"), "aspire", "13.6.1")],
            install.Arguments);
        var deploy = Assert.Single(_h.DeploySpecs);
        Assert.StartsWith(Path.Combine(_h.Dir.Combine("tools"), "aspire", "13.6.1"), deploy.FileName);
        Assert.StartsWith(Path.Combine(_h.Dir.Combine("tools"), "aspire", "13.6.1"), deploy.Environment["PATH"]);
    }

    [Fact]
    public async Task Matching_cli_on_path_installs_nothing()
    {
        await _h.AddProfileAsync();
        await DeployAsync();
        Assert.DoesNotContain(_h.Runner.Specs, s => FakeProcessRunner.Is(s, "tool", "install"));
        Assert.Equal("aspire", Assert.Single(_h.DeploySpecs).FileName);
    }
}
