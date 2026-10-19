using System.Diagnostics;
using System.Text.Json;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Services;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;
using AspireDeployService.Core.Tools;
using AspireDeployService.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AspireDeployService.Tests;

/// <summary>Regression tests for the independent review of PR #1.</summary>
public class VersionPathSafetyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("13.6.1", true)]
    [InlineData("13.6.1-preview.1.26506.6", true)]
    [InlineData("13.6.1+3751e615", true)]
    [InlineData("13.6", false)]
    [InlineData("13.0.0-x/../../../../Users/me/Documents", false)]
    [InlineData("13.0.0-..", false)]
    [InlineData("13.0.0\\..\\x", false)]
    [InlineData("C:\\13.0.0", false)]
    public void Only_plain_semantic_versions_are_accepted(string version, bool ok) =>
        Assert.Equal(ok, AspireVersionRules.IsStrictVersion(version));

    [Fact]
    public void Scanner_refuses_a_version_that_is_a_path()
    {
        _dir.Write("A/A.csproj", """<Project Sdk="Aspire.AppHost.Sdk/13.0.0-x/../../../../escape"><PropertyGroup /></Project>""");
        _dir.Write("A/AppHost.cs", AppHostFixtures.AcaAppHostCs);
        var ex = Assert.Throws<DeployerException>(() => AppHostScanner.Scan(_dir.Path, null, AspireDeployOptions.From(null)));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
    }

    [Theory]
    [InlineData("13.0.0-x/../../escape")]
    [InlineData("..")]
    [InlineData("")]
    public void Tool_folder_never_leaves_the_tools_root(string version)
    {
        var victim = _dir.Combine("escape");
        Directory.CreateDirectory(victim);
        var ex = Assert.Throws<DeployerException>(() => AspireCliProvisioner.ToolFolder(_dir.Combine("tools"), version));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.True(Directory.Exists(victim));
    }

    [Fact]
    public void Tool_folder_for_a_valid_version() =>
        Assert.Equal(Path.Combine(_dir.Combine("tools"), "aspire", "13.6.1"), AspireCliProvisioner.ToolFolder(_dir.Combine("tools"), "13.6.1"));
}

public class ProfileNamePathSafetyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("C:\\x")]
    [InlineData("a/b")]
    public void Work_key_refuses_names_that_are_paths(string name)
    {
        var ex = Assert.Throws<DeployerException>(() => WorkKeys.For(_dir.Path, name, null));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
    }

    [Fact]
    public async Task Deleting_a_hand_edited_profile_named_dot_dot_deletes_nothing()
    {
        var workRoot = _dir.Combine("ads", "w");
        var sibling = _dir.Combine("ads", "tools");
        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(sibling);
        var settings = _dir.Combine("appsettings.Local.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new
        {
            AspireDeploy = new
            {
                WorkRoot = workRoot,
                Profiles = new Dictionary<string, object> { [".."] = new { Source = new { Kind = "LocalFolder", Location = _dir.Path } } }
            }
        }));
        var service = new ProfileService(new ProfileStore(new FixedLocator(settings)));

        var ex = await Assert.ThrowsAsync<DeployerException>(() => service.DeleteAsync("..", deleteWorkFolder: true, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.True(Directory.Exists(workRoot));
        Assert.True(Directory.Exists(sibling));
    }
}

public class DeployVersionRecordTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose()
    {
        File.SetAttributes(_h.SettingsPath, FileAttributes.Normal);
        _h.Dispose();
    }

    /// <summary>D18: a settings write that fails with an I/O error after a successful deploy is only a warning.</summary>
    [Fact]
    public async Task Read_only_settings_file_does_not_fail_a_successful_deploy()
    {
        await _h.AddProfileAsync();
        _h.OnDeploy = (_, _) =>
        {
            File.SetAttributes(_h.SettingsPath, FileAttributes.ReadOnly);
            return Task.FromResult(FakeProcessRunner.Ok("Deployment completed."));
        };

        var result = await _h.Deploy().DeployAsync(new DeployRequest("shop", null, false, false), _h.Observer, CancellationToken.None);

        Assert.Equal(OutcomeCode.Success, result.Code);
        Assert.True(_h.Observer.Contains("could not be saved to the profile"));
        var history = File.ReadAllText(Path.Combine(_h.WorkRoot, "shop", "history.jsonl"));
        Assert.Contains("\"exitCode\":0", history);
    }
}

[Collection("Environment")]
public class ProcessRunnerDrainTests
{
    /// <summary>A grandchild that keeps the output pipe open must not hang the run after the tool exits.</summary>
    [Fact]
    public async Task Run_finishes_when_a_grandchild_keeps_the_output_open()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var before = Process.GetProcessesByName("PING").Select(p => p.Id).ToHashSet();
        var previous = ProcessRunner.OutputDrainTimeout;
        ProcessRunner.OutputDrainTimeout = TimeSpan.FromSeconds(2);
        try
        {
            var log = new ListLogSink();
            var runner = new ProcessRunner(new SecretRedactor(), NullLogger<ProcessRunner>.Instance);
            // "start /b" leaves ping running with cmd's inherited stdout after cmd itself exits.
            var spec = new ProcessSpec("cmd", ["/c", "start /b ping -n 60 127.0.0.1"], Path.GetTempPath());

            var run = runner.RunAsync(spec, log, CancellationToken.None);
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));

            Assert.Same(run, finished);
            Assert.Equal(0, (await run).ExitCode);
            Assert.Contains(log.Lines, l => l.Line.Contains("still holds its output open"));
        }
        finally
        {
            ProcessRunner.OutputDrainTimeout = previous;
            foreach (var p in Process.GetProcessesByName("PING").Where(p => !before.Contains(p.Id)))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
            }
        }
    }
}

public class ProfileEditSessionTests
{
    private static DeployProfile Saved() => new()
    {
        Source = new ProfileSource { Kind = SourceKind.GitHub, Location = "owner/repo", GitHubToken = "ghp-original-token" },
        Destination = new ProfileDestination { SubscriptionId = "00000000-0000-0000-0000-000000000001", Location = "eastus2", ResourceGroup = "rg" },
        Parameters = { ["apiKey"] = "secret-1", ["region"] = "west" },
        SecretParameters = ["apiKey"]
    };

    private static DeployProfile Apply(ProfileEditSession s, DeployProfile? fresh = null)
    {
        var target = (fresh ?? Saved()).Clone();
        s.ApplyTo(target);
        return target;
    }

    [Fact]
    public void Saved_secrets_are_masked_and_untouched_by_default()
    {
        var s = new ProfileEditSession(Saved());
        Assert.True(s.IsMasked("apiKey"));
        Assert.False(s.IsMasked("region"));
        Assert.Equal("", s.Edited.Parameters["apiKey"]);
        Assert.Null(s.Edited.Source.GitHubToken);

        var saved = Apply(s);
        Assert.Equal("secret-1", saved.Parameters["apiKey"]);
        Assert.Equal("ghp-original-token", saved.Source.GitHubToken);
        Assert.Equal(["apiKey"], saved.SecretParameters);
    }

    [Fact]
    public void Ticking_secret_on_a_plain_value_keeps_its_edits()
    {
        var s = new ProfileEditSession(Saved());
        s.SetSecret("region", true);
        Assert.False(s.IsMasked("region"));
        s.Edited.Parameters["region"] = "east";

        var saved = Apply(s);
        Assert.Equal("east", saved.Parameters["region"]);
        Assert.Contains("region", saved.SecretParameters);
    }

    [Fact]
    public void Unticking_secret_on_a_masked_value_still_applies_its_replacement()
    {
        var s = new ProfileEditSession(Saved());
        s.SetSecret("apiKey", false);
        Assert.True(s.IsMasked("apiKey"));
        s.Replace("apiKey", "secret-2");

        var saved = Apply(s);
        Assert.Equal("secret-2", saved.Parameters["apiKey"]);
        Assert.DoesNotContain("apiKey", saved.SecretParameters);
    }

    [Fact]
    public void Clear_then_save_empties_a_secret()
    {
        var s = new ProfileEditSession(Saved());
        s.Clear("apiKey", true);
        Assert.Equal("", Apply(s).Parameters["apiKey"]);
    }

    [Fact]
    public void Removed_and_re_added_secret_takes_the_new_typed_value()
    {
        var s = new ProfileEditSession(Saved());
        s.Replace("apiKey", "stale-replacement");
        s.Remove("apiKey");
        Assert.True(s.Add("apiKey"));
        Assert.False(s.IsMasked("apiKey"));
        Assert.Null(s.ReplacementFor("apiKey"));
        s.Edited.Parameters["apiKey"] = "typed-value";

        var saved = Apply(s);
        Assert.Equal("typed-value", saved.Parameters["apiKey"]);
        Assert.DoesNotContain("apiKey", saved.SecretParameters);
    }

    [Fact]
    public void Removed_parameter_is_gone()
    {
        var s = new ProfileEditSession(Saved());
        s.Remove("apiKey");
        var saved = Apply(s);
        Assert.False(saved.Parameters.ContainsKey("apiKey"));
        Assert.DoesNotContain("apiKey", saved.SecretParameters);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Replace_with_nothing_typed_keeps_the_secret_and_the_token(string typed)
    {
        var s = new ProfileEditSession(Saved());
        s.Replace("apiKey", typed);
        s.TokenReplacement = typed;
        var saved = Apply(s);
        Assert.Equal("secret-1", saved.Parameters["apiKey"]);
        Assert.Equal("ghp-original-token", saved.Source.GitHubToken);
    }

    [Fact]
    public void Token_can_be_replaced_or_cleared()
    {
        var replaced = new ProfileEditSession(Saved()) { TokenReplacement = " ghp-new " };
        Assert.Equal("ghp-new", Apply(replaced).Source.GitHubToken);
        var cleared = new ProfileEditSession(Saved()) { TokenCleared = true };
        Assert.Null(Apply(cleared).Source.GitHubToken);
    }

    [Fact]
    public void Secret_flags_added_by_another_window_survive_a_save()
    {
        var s = new ProfileEditSession(Saved());
        s.Edited.Destination.Location = "westus3";

        // Meanwhile another window added a secret parameter.
        var fresh = Saved();
        fresh.Parameters["dbPassword"] = "other-window-secret";
        fresh.SecretParameters.Add("dbPassword");

        var saved = Apply(s, fresh);
        Assert.Equal("westus3", saved.Destination.Location);
        Assert.Contains("dbPassword", saved.SecretParameters);
        Assert.Equal("other-window-secret", saved.Parameters["dbPassword"]);
    }
}
