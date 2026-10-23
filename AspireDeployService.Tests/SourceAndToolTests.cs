using System.Net;
using System.Net.Sockets;
using System.Text;
using AspireDeployService.Core.Deploy;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Services;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;
using AspireDeployService.Core.Tools;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests;

public class GitHubSourceTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private RunContext Run() => _h.Runs.Create(_h.Dir.Combine("wk"), _h.Observer);

    private GitHubSource Source(string? token = null, long max = 512 * 1024 * 1024) =>
        new(new StubHttpClientFactory(_h.GitHub)) { Token = token, MaxDownloadBytes = max };

    private static SourceSpec Spec(string? gitRef = "main") => new(SourceKind.GitHub, "owner/repo", gitRef, null);

    [Fact]
    public async Task Ref_resolves_to_a_sha_and_the_inner_folder_becomes_src()
    {
        _h.ServeRepository();
        await using var run = Run();
        var id = await Source("tok").MaterializeAsync(Spec(), _h.Dir.Combine("wk"), run.Services, CancellationToken.None);

        Assert.Equal(Harness.Sha, id.CommitSha);
        Assert.Equal(_h.Dir.Combine("wk", "src"), id.RootFolder);
        Assert.True(File.Exists(_h.Dir.Combine("wk", "src", "Shop.AppHost", "Shop.AppHost.csproj")));
        Assert.All(_h.GitHub.Requests, r => Assert.Equal("Bearer tok", r.Authorization));

        // Same SHA again: no second download, and an old src is kept.
        var before = _h.GitHub.Requests.Count;
        await Source("tok").MaterializeAsync(Spec(), _h.Dir.Combine("wk"), run.Services, CancellationToken.None);
        Assert.Equal(before + 1, _h.GitHub.Requests.Count);
    }

    [Fact]
    public async Task Default_branch_uses_HEAD()
    {
        _h.ServeRepository();
        await using var run = Run();
        var id = await Source().MaterializeAsync(Spec(null), _h.Dir.Combine("wk"), run.Services, CancellationToken.None);
        Assert.Contains(_h.GitHub.Requests, r => r.Url.EndsWith("/commits/HEAD"));
        Assert.Contains("default branch", id.Display);
    }

    [Theory]
    [InlineData(404, null, null, null, "may be private")]
    [InlineData(404, "tok", null, null, "Contents: read")]
    [InlineData(403, "tok", "0", null, "rate limit")]
    [InlineData(403, "tok", "10", "required; url=https://github.com/orgs/x/sso", "single sign-on")]
    [InlineData(401, "tok", null, null, "refused the token")]
    public async Task Errors_have_their_own_messages(int status, string? token, string? remaining, string? sso, string expected)
    {
        _h.GitHub.On("https://api.github.com/repos/owner/repo/commits/", _ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)status);
            if (remaining is not null) r.Headers.Add("x-ratelimit-remaining", remaining);
            if (sso is not null) r.Headers.Add("X-GitHub-SSO", sso);
            return r;
        });
        await using var run = Run();
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            Source(token).MaterializeAsync(Spec(), _h.Dir.Combine("wk"), run.Services, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Download_cap_is_enforced()
    {
        _h.ServeRepository();
        await using var run = Run();
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            Source(max: 100).MaterializeAsync(Spec(), _h.Dir.Combine("wk"), run.Services, CancellationToken.None));
        Assert.Equal(OutcomeCode.DiscoveryFailed, ex.Code);
    }

    [Fact]
    public async Task Archive_without_one_top_folder_is_code_4()
    {
        _h.GitHub.On("https://api.github.com/repos/owner/repo/commits/", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{Harness.Sha}\"}}") });
        _h.GitHub.On("https://api.github.com/repos/owner/repo/zipball/", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zipball.Create(("a.txt", "1"), ("b/c.txt", "2"))) });
        await using var run = Run();
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            Source().MaterializeAsync(Spec(), _h.Dir.Combine("wk"), run.Services, CancellationToken.None));
        Assert.Equal(OutcomeCode.DiscoveryFailed, ex.Code);
    }

    /// <summary>Plan §9: .NET drops Authorization when a redirect goes to another host (api.github.com → codeload).</summary>
    [Fact]
    public async Task Authorization_is_not_forwarded_on_a_cross_host_redirect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var seen = new List<string>();

        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var buffer = new byte[8192];
                var n = await stream.ReadAsync(buffer);
                var request = Encoding.ASCII.GetString(buffer, 0, n);
                seen.Add(request);
                var response = i == 0
                    ? $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/archive\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        });

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/zipball");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token");
        using var response = await http.SendAsync(request);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Contains("Authorization: Bearer secret-token", seen[0]);
        Assert.DoesNotContain("Authorization", seen[1]);
    }

    [Fact]
    public async Task Replacing_src_waits_while_the_old_folder_is_still_in_the_way()
    {
        // Stands in for a just-deleted src that another process still holds (delete pending).
        var from = Directory.CreateDirectory(_h.Dir.Combine("inner")).FullName;
        File.WriteAllText(Path.Combine(from, "a.txt"), "x");
        var to = Directory.CreateDirectory(_h.Dir.Combine("src-moved")).FullName;
        var release = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Directory.Delete(to);
        });

        await GitHubSource.MoveWithRetriesAsync(from, to, CancellationToken.None);
        await release;
        Assert.True(File.Exists(Path.Combine(to, "a.txt")));
    }

    [Fact]
    public async Task Replacing_src_gives_up_with_a_clear_message()
    {
        var from = Directory.CreateDirectory(_h.Dir.Combine("inner2")).FullName;
        var to = Directory.CreateDirectory(_h.Dir.Combine("src-busy")).FullName;
        var ex = await Assert.ThrowsAsync<DeployerException>(() => GitHubSource.MoveWithRetriesAsync(from, to, CancellationToken.None, attempts: 2));
        Assert.Equal(OutcomeCode.DiscoveryFailed, ex.Code);
        Assert.Contains("still in use", ex.Message);
    }
}

public class GitHubTokenResolverTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private (RunContext Run, FakeProcessRunner Runner) Context(Func<ProcessSpec, CancellationToken, Task<ProcessResult>> gh)
    {
        var runner = new FakeProcessRunner(gh);
        return (new FakeRunContextFactory(runner).Create(_dir.Path, new ListRunObserver()), runner);
    }

    [Fact]
    public async Task Order_is_profile_then_settings_then_gh()
    {
        var (run, runner) = Context((_, _) => Task.FromResult(FakeProcessRunner.Ok("gh-token-value")));
        var resolver = new GitHubTokenResolver();
        Assert.Equal("the profile", (await resolver.ResolveAsync("p", "g", run, CancellationToken.None)).Origin);
        Assert.Equal("the settings file", (await resolver.ResolveAsync(null, "g", run, CancellationToken.None)).Origin);
        Assert.Empty(runner.Specs);
        var fromGh = await resolver.ResolveAsync(null, null, run, CancellationToken.None);
        Assert.Equal("gh-token-value", fromGh.Value);
        Assert.Equal("[REDACTED]", run.Redactor.Redact("gh-token-value"));
    }

    [Fact]
    public async Task Missing_or_failing_gh_means_no_token()
    {
        var (missing, _) = Context((_, _) => throw new System.ComponentModel.Win32Exception(2));
        Assert.Null((await new GitHubTokenResolver().ResolveAsync(null, null, missing, CancellationToken.None)).Value);
        var (failing, _) = Context((_, _) => Task.FromResult(FakeProcessRunner.Fail(1, "not logged in")));
        Assert.Null((await new GitHubTokenResolver().ResolveAsync(null, null, failing, CancellationToken.None)).Value);
    }
}

public class TrustStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Trust_is_per_repository_and_commit()
    {
        var store = new TrustStore();
        Assert.False(store.IsTrusted(_dir.Path, "owner/repo", Harness.Sha));
        await store.AddAsync(_dir.Path, "owner/repo", Harness.Sha, CancellationToken.None);
        Assert.True(store.IsTrusted(_dir.Path, "Owner/Repo", Harness.Sha));
        Assert.False(store.IsTrusted(_dir.Path, "owner/repo", new string('f', 40)));
        Assert.False(store.IsTrusted(_dir.Path, "owner/other", Harness.Sha));
    }
}

public class DiscoveryServiceTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private static SourceSpec GitHub => new(SourceKind.GitHub, "owner/repo", "main", null);

    [Fact]
    public async Task Declined_trust_is_code_7_and_builds_nothing()
    {
        _h.ServeRepository();
        _h.Interaction = new FakeUserInteraction(answer: false);
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _h.Discovery().DiscoverAsync(new DiscoverRequest(GitHub, null, StaticOnly: false), _h.Observer, CancellationToken.None));
        Assert.Equal(OutcomeCode.Declined, ex.Code);
        Assert.Contains(Harness.Sha, Assert.Single(_h.Interaction.Asked).Message);
        Assert.DoesNotContain(_h.Runner.Specs, s => FakeProcessRunner.Is(s, "do", "publish-manifest"));
    }

    [Fact]
    public async Task Trusted_commit_is_not_asked_again_and_ad_hoc_runs_share_the_file()
    {
        _h.ServeRepository();
        var first = await _h.Discovery().DiscoverAsync(new DiscoverRequest(GitHub, null, false), _h.Observer, CancellationToken.None);
        Assert.True(first.ManifestRan);
        Assert.Equal(4, first.Parameters.Count);
        Assert.Single(_h.Interaction.Asked);

        await _h.Discovery().DiscoverAsync(new DiscoverRequest(GitHub, null, false), _h.Observer, CancellationToken.None);
        Assert.Single(_h.Interaction.Asked);
        Assert.True(File.Exists(TrustStore.FilePath(_h.WorkRoot)));
    }

    [Fact]
    public async Task Static_only_runs_no_process_and_local_folders_need_no_trust()
    {
        var local = new SourceSpec(SourceKind.LocalFolder, _h.SourceFolder, null, null);
        var result = await _h.Discovery().DiscoverAsync(new DiscoverRequest(local, null, StaticOnly: true), _h.Observer, CancellationToken.None);
        Assert.False(result.ManifestRan);
        Assert.Empty(_h.Runner.Specs);

        var full = await _h.Discovery().DiscoverAsync(new DiscoverRequest(local, null, StaticOnly: false), _h.Observer, CancellationToken.None);
        Assert.True(full.ManifestRan);
        Assert.Empty(_h.Interaction.Asked);
        Assert.Contains("\"configKey\": \"ConnectionStrings:db\"", full.ToJson());
    }

    [Fact]
    public async Task Static_scan_never_prompts_for_trust_on_github()
    {
        _h.ServeRepository();
        var scan = await _h.Discovery().DiscoverAsync(new DiscoverRequest(GitHub, null, StaticOnly: true), _h.Observer, CancellationToken.None);
        Assert.False(scan.ManifestRan);
        Assert.Equal(Harness.Sha, scan.Source.CommitSha);
        Assert.Empty(_h.Interaction.Asked);
        Assert.False(File.Exists(TrustStore.FilePath(_h.WorkRoot)));
    }

    [Fact]
    public async Task Trusted_sha_from_scan_records_trust_without_prompt()
    {
        _h.ServeRepository();
        var pinned = GitHub with { Ref = Harness.Sha };
        var result = await _h.Discovery().DiscoverAsync(
            new DiscoverRequest(pinned, null, StaticOnly: false, TrustedCommitSha: Harness.Sha.ToUpperInvariant()),
            _h.Observer, CancellationToken.None);
        Assert.True(result.ManifestRan);
        Assert.Empty(_h.Interaction.Asked);
        Assert.True(new TrustStore().IsTrusted(_h.WorkRoot, "owner/repo", Harness.Sha));
        Assert.Contains(_h.Runner.Specs, s => FakeProcessRunner.Is(s, "do", "publish-manifest"));
    }

    [Fact]
    public async Task Different_sha_still_prompts()
    {
        _h.ServeRepository();
        _h.Interaction = new FakeUserInteraction(answer: false);
        var scanned = new string('a', 40);
        var ex = await Assert.ThrowsAsync<DeployerException>(() => _h.Discovery().DiscoverAsync(
            new DiscoverRequest(GitHub with { Ref = scanned }, null, StaticOnly: false, TrustedCommitSha: scanned),
            _h.Observer, CancellationToken.None));
        Assert.Equal(OutcomeCode.Declined, ex.Code);
        Assert.Equal(7, (int)ex.Code);
        Assert.Contains(Harness.Sha, Assert.Single(_h.Interaction.Asked).Message);
        Assert.DoesNotContain(_h.Runner.Specs, s => FakeProcessRunner.Is(s, "do", "publish-manifest"));
    }
}

public class DeployEnvironmentTests
{
    [Theory]
    [InlineData("Parameters:region-name", "Parameters__region_name")]
    [InlineData("Parameters:apiKey", "Parameters__apiKey")]
    [InlineData("ConnectionStrings:db", "ConnectionStrings__db")]
    [InlineData("ConnectionStrings:my-db", "ConnectionStrings__my-db")]
    [InlineData("MyApp:Api-Key", "MyApp__Api-Key")]
    public void Config_key_to_environment_variable(string key, string expected) =>
        Assert.Equal(expected, DeployEnvironment.ToEnvironmentVariable(key));

    [Fact]
    public void Overrides_runtime_and_empty_values()
    {
        var profile = new DeployProfile
        {
            Destination = new ProfileDestination { SubscriptionId = "s", Location = "l", ResourceGroup = "r", AllowResourceGroupCreation = false },
            Parameters = { ["apiKey"] = "v", ["empty"] = "", ["custom"] = "c" },
            ParameterConfigKeys = { ["custom"] = "MyApp:ApiKey" }
        };
        var env = DeployEnvironment.Build(profile, [], null, name => name == "ASPIRE_CONTAINER_RUNTIME" ? "podman" : null);
        Assert.Equal("v", env["Parameters__apiKey"]);
        Assert.Equal("c", env["MyApp__ApiKey"]);
        Assert.False(env.ContainsKey("Parameters__empty"));
        Assert.Equal("podman", env["ASPIRE_CONTAINER_RUNTIME"]);
        Assert.Equal("false", env["Azure__AllowResourceGroupCreation"]);
        Assert.False(env.ContainsKey("Azure__TenantId"));

        profile.ContainerRuntime = "docker";
        Assert.Equal("docker", DeployEnvironment.Build(profile, [], null, _ => "podman")["ASPIRE_CONTAINER_RUNTIME"]);
    }
}

public class AspireCliProvisionerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Install_failure_is_code_3()
    {
        var runner = new FakeProcessRunner((s, _) => Task.FromResult(FakeProcessRunner.Is(s, "--version")
            ? FakeProcessRunner.Ok("13.0.0")
            : FakeProcessRunner.Fail(1, "error NU1101: Unable to find package aspire.cli")));
        await using var run = new FakeRunContextFactory(runner).Create(_dir.Path, new ListRunObserver());
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            new AspireCliProvisioner().EnsureAsync("13.9.9-daily", _dir.Combine("tools"), run, CancellationToken.None));
        Assert.Equal(OutcomeCode.MissingPrerequisite, ex.Code);
        Assert.Contains("NU1101", ex.Message);
    }

    [Fact]
    public async Task Missing_aspire_on_path_installs()
    {
        var runner = new FakeProcessRunner((s, _) =>
        {
            if (FakeProcessRunner.Is(s, "--version"))
                throw new System.ComponentModel.Win32Exception(2);
            var dir = s.Arguments[^1];
            File.WriteAllText(Path.Combine(dir, "aspire.cmd"), "");
            File.WriteAllText(Path.Combine(dir, "aspire"), "");
            return Task.FromResult(FakeProcessRunner.Ok());
        });
        await using var run = new FakeRunContextFactory(runner).Create(_dir.Path, new ListRunObserver());
        var cli = await new AspireCliProvisioner().EnsureAsync("13.6.1", _dir.Combine("tools"), run, CancellationToken.None);
        Assert.Equal(Path.Combine(_dir.Combine("tools"), "aspire", "13.6.1"), cli.Folder);

        // Already installed: the second call installs nothing.
        var installs = runner.Specs.Count(s => FakeProcessRunner.Is(s, "tool"));
        await new AspireCliProvisioner().EnsureAsync("13.6.1", _dir.Combine("tools"), run, CancellationToken.None);
        Assert.Equal(installs, runner.Specs.Count(s => FakeProcessRunner.Is(s, "tool")));
    }
}

public class ProfileDraftTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Draft_asks_only_for_parameters_without_default_or_generator()
    {
        var local = new SourceSpec(SourceKind.LocalFolder, _h.SourceFolder, null, null);
        var discovery = await _h.Discovery().DiscoverAsync(new DiscoverRequest(local, null, false), _h.Observer, CancellationToken.None);
        var draft = await _h.Profiles.DraftFromDiscoveryAsync("shop", local, discovery, CancellationToken.None);

        Assert.Equal(["apiKey", "region-name", "db"], draft.ToAsk.Select(p => p.Name));
        Assert.Equal(["gen-pass"], draft.Optional.Select(p => p.Name));
        Assert.Equal("Shop.AppHost/Shop.AppHost.csproj", draft.Profile.Source.AppHostPath);
        Assert.Equal("rg-shop", draft.Profile.Destination.ResourceGroup);
        Assert.Equal(["apiKey", "gen-pass", "db"], draft.Profile.SecretParameters);
        Assert.Empty(draft.Warnings);

        await _h.AddProfileAsync("other", p => p.Source.AppHostPath = "Shop.AppHost/Shop.AppHost.csproj");
        var second = await _h.Profiles.DraftFromDiscoveryAsync("shop", local, discovery, CancellationToken.None);
        Assert.Contains(second.Warnings, w => w.Contains("share Aspire's deploy cache"));
    }
}
