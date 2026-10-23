using AspireDeployService.Core.Azure;
using AspireDeployService.Core.Errors;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests.Azure;

/// <summary>Wizard plan §6.3 and D15: az login from the app, and the sign-in error category.</summary>
public class AzureSignInServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<ProcessSpec> _calls = [];
    private readonly AzureAccountService _account;
    private readonly AzureSignInService _signIn;

    public Func<ProcessSpec, CancellationToken, Task<ProcessResult>> OnLogin { get; set; } =
        (_, _) => Task.FromResult(FakeProcessRunner.Ok(AzureAccountListTests.Az("login.json")));

    public Func<string, ProcessResult?> Override { get; set; } = _ => null;

    public AzureSignInServiceTests()
    {
        var path = _dir.Write("appsettings.Local.json", $$"""{ "AspireDeploy": { "WorkRoot": "{{_dir.Combine("w").Replace("\\", "/")}}" } }""");
        var runner = new FakeProcessRunner((spec, ct) =>
        {
            lock (_calls)
                _calls.Add(spec);
            var args = string.Join(' ', spec.Arguments);
            if (Override(args) is { } overridden)
                return Task.FromResult(overridden);
            if (args.StartsWith("login", StringComparison.Ordinal))
                return OnLogin(spec, ct);
            return Task.FromResult(args switch
            {
                _ when args.StartsWith("account list ") => FakeProcessRunner.Ok(AzureAccountListTests.Az("login.json")),
                _ => FakeProcessRunner.Fail(1, "ERROR: unexpected")
            });
        });
        var store = new ProfileStore(new FixedLocator(path));
        var runs = new FakeRunContextFactory(runner);
        _account = new AzureAccountService(store, runs, TimeProvider.System);
        _signIn = new AzureSignInService(store, runs, _account);
    }

    public void Dispose() => _dir.Dispose();

    private int Count(string prefix)
    {
        lock (_calls)
            return _calls.Count(c => string.Join(' ', c.Arguments).StartsWith(prefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignIn_builds_arguments()
    {
        var result = await _signIn.SignInAsync(" contoso.onmicrosoft.com ", useDeviceCode: true, NullRunObserver.Instance, CancellationToken.None);
        var spec = Assert.Single(_calls);
        Assert.Equal(["login", "--tenant", "contoso.onmicrosoft.com", "--use-device-code", "--output", "json"], spec.Arguments);
        Assert.Equal("off", spec.Environment["AZURE_CORE_LOGIN_EXPERIENCE_V2"]);
        Assert.False(spec.LogStdOut);
        Assert.Equal(AzureSignInService.Timeout, spec.Timeout);
        Assert.Equal(new AzureSignInResult("dev@contoso.example", 2), result);
    }

    [Fact]
    public void SignIn_arguments_without_options() =>
        Assert.Equal(["login", "--output", "json"], AzureSignInService.Arguments(null, useDeviceCode: false));

    [Theory]
    [InlineData("contoso tenant")]
    [InlineData("contoso;rm")]
    [InlineData("a&b")]
    public async Task SignIn_rejects_bad_tenant(string tenant)
    {
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _signIn.SignInAsync(tenant, false, NullRunObserver.Instance, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task SignIn_success_invalidates_all_lists()
    {
        await _account.ListSubscriptionsAsync(NullRunObserver.Instance, CancellationToken.None);
        await _account.ListSubscriptionsAsync(NullRunObserver.Instance, CancellationToken.None);
        Assert.Equal(1, Count("account list"));

        await _signIn.SignInAsync(null, false, NullRunObserver.Instance, CancellationToken.None);
        await _account.ListSubscriptionsAsync(NullRunObserver.Instance, CancellationToken.None);
        Assert.Equal(2, Count("account list"));
    }

    [Fact]
    public async Task SignIn_failure_message()
    {
        OnLogin = (_, _) => Task.FromResult(FakeProcessRunner.Fail(1, "WARNING: x\nERROR: AADSTS50020: User account does not exist in tenant."));
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _signIn.SignInAsync(null, false, NullRunObserver.Instance, CancellationToken.None));
        Assert.Equal(OutcomeCode.MissingPrerequisite, ex.Code);
        Assert.Equal(ErrorCategory.Permissions, ex.Category);
        Assert.Equal("Sign-in did not finish: ERROR: AADSTS50020: User account does not exist in tenant.", ex.Message);
    }

    [Fact]
    public async Task SignIn_timeout_message()
    {
        OnLogin = (_, _) => throw new TimeoutException("az did not finish within 5 minutes.");
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _signIn.SignInAsync(null, false, NullRunObserver.Instance, CancellationToken.None));
        Assert.Equal(ErrorCategory.Timeout, ex.Category);
        Assert.Equal("Sign-in timed out after 5 minutes. Try again, or tick Use a device code.", ex.Message);
    }

    [Fact]
    public async Task SignIn_missing_az_is_not_a_sign_in_error()
    {
        OnLogin = (_, _) => throw new System.ComponentModel.Win32Exception(2);
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _signIn.SignInAsync(null, false, NullRunObserver.Instance, CancellationToken.None));
        Assert.Equal(ErrorCategory.None, ex.Category);
    }

    [Fact]
    public async Task ListSubscriptions_sign_in_errors_are_permissions()
    {
        Override = a => a.StartsWith("account list") ? FakeProcessRunner.Fail(1, "ERROR: Please run 'az login' to setup account.") : null;
        var notSignedIn = await Assert.ThrowsAsync<DeployerException>(() =>
            _account.ListSubscriptionsAsync(NullRunObserver.Instance, CancellationToken.None, refresh: true));
        Assert.Equal(ErrorCategory.Permissions, notSignedIn.Category);

        Override = a => a.StartsWith("account list") ? FakeProcessRunner.Ok("[]") : null;
        var none = await Assert.ThrowsAsync<DeployerException>(() =>
            _account.ListSubscriptionsAsync(NullRunObserver.Instance, CancellationToken.None, refresh: true));
        Assert.Equal(ErrorCategory.Permissions, none.Category);
        Assert.Contains("sees no subscriptions", none.Message);

        Override = a => a.StartsWith("account list") ? throw new System.ComponentModel.Win32Exception(2) : null;
        var missing = await Assert.ThrowsAsync<DeployerException>(() =>
            _account.ListSubscriptionsAsync(NullRunObserver.Instance, CancellationToken.None, refresh: true));
        Assert.Equal("The Azure CLI (az) is not installed.", missing.Message);
        Assert.NotEqual(ErrorCategory.Permissions, missing.Category);
    }

    [Fact]
    public async Task NotSignedIn_list_errors_are_permissions()
    {
        const string sub = "00000000-0000-0000-0000-000000000001";
        Override = a => a.StartsWith("group list") ? FakeProcessRunner.Fail(1,
            "ERROR: AADSTS50076: Due to a configuration change made by your administrator, you must use multi-factor authentication.") : null;
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _account.ListResourceGroupsAsync(sub, NullRunObserver.Instance, CancellationToken.None));
        Assert.Equal(ErrorCategory.Permissions, ex.Category);
        Assert.EndsWith("Sign in to Azure again.", ex.Message);

        Override = a => a.StartsWith("group list") ? FakeProcessRunner.Fail(1, "ERROR: something odd") : null;
        var other = await Assert.ThrowsAsync<DeployerException>(() =>
            _account.ListResourceGroupsAsync(sub, NullRunObserver.Instance, CancellationToken.None, refresh: true));
        Assert.Equal(ErrorCategory.None, other.Category);
    }

    [Fact]
    public void Parse_reads_the_first_user_and_the_count()
    {
        Assert.Equal(new AzureSignInResult(null, 0), AzureSignInService.Parse("not json"));
        Assert.Equal(new AzureSignInResult(null, 1), AzureSignInService.Parse("""[ { "id": "x" } ]"""));
    }
}
