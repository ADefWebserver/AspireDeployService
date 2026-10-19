using System.Text.Json;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Services;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests;

public class SettingsLocatorTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private SettingsLocator Locator(string? envValue, string baseDir) =>
        new(name => name == SettingsLocator.EnvironmentVariable ? envValue : null, baseDir, _dir.Combine("appdata"));

    [Fact]
    public void Environment_variable_wins()
    {
        var path = _dir.Combine("custom.json");
        var location = Locator(path, _dir.Path).Resolve();
        Assert.Equal(SettingsRule.EnvironmentVariable, location.Rule);
        Assert.Equal(path, location.Path);
        Assert.False(location.Exists);
    }

    [Fact]
    public void Project_folder_is_found_by_walking_up_even_when_the_file_does_not_exist_yet()
    {
        _dir.Write("src/AspireDeployService/AspireDeployService.csproj", "<Project />");
        var baseDir = _dir.Combine("src", "AspireDeployService", "bin", "Debug", "net10.0-windows10.0.19041.0", "win-x64");
        Directory.CreateDirectory(baseDir);

        var location = Locator(null, baseDir).Resolve();

        Assert.Equal(SettingsRule.ProjectFolder, location.Rule);
        Assert.Equal(_dir.Combine("src", "AspireDeployService", "appsettings.Local.json"), location.Path);
        Assert.False(location.Exists);
    }

    [Fact]
    public void Falls_back_to_local_app_data()
    {
        var location = Locator(null, _dir.Combine("publish")).Resolve();
        Assert.Equal(SettingsRule.LocalAppData, location.Rule);
        Assert.Equal(_dir.Combine("appdata", "AspireDeployService", "appsettings.Local.json"), location.Path);
    }
}

public class ProfileStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _path;
    private readonly ProfileStore _store;
    private readonly ProfileService _service;

    public ProfileStoreTests()
    {
        _path = _dir.Combine("appsettings.Local.json");
        _store = new ProfileStore(new FixedLocator(_path));
        _service = new ProfileService(_store);
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>Points WorkRoot at the test folder.</summary>
    private void SeedWorkRoot() =>
        File.WriteAllText(_path, JsonSerializer.Serialize(new { AspireDeploy = new { WorkRoot = _dir.Combine("w") } }));

    internal static DeployProfile ValidProfile(string folder) => new()
    {
        Source = new ProfileSource { Kind = SourceKind.LocalFolder, Location = folder },
        Destination = new ProfileDestination
        {
            SubscriptionId = "00000000-0000-0000-0000-000000000001",
            Location = "eastus2",
            ResourceGroup = "rg-shop"
        }
    };

    [Fact]
    public async Task First_save_creates_the_file()
    {
        Assert.False(File.Exists(_path));
        await _service.SaveAsync("shop", ValidProfile(_dir.Path), overwrite: false, CancellationToken.None);
        Assert.True(File.Exists(_path));
        Assert.NotNull(await _service.GetAsync("shop", CancellationToken.None));
    }

    [Fact]
    public async Task Round_trip_keeps_empty_versus_absent_parameters_and_unknown_keys()
    {
        File.WriteAllText(_path, """
            {
              // a comment the developer wrote
              "Logging": { "LogLevel": { "Default": "Warning" } },
              "AspireDeploy": {
                "Custom": 42,
                "Profiles": {
                  "shop": {
                    "Source": { "Kind": "LocalFolder", "Location": "C:\\src\\shop", "Mine": true },
                    "Destination": { "SubscriptionId": "00000000-0000-0000-0000-000000000001", "Location": "eastus2", "ResourceGroup": "rg-shop" },
                    "Parameters": { "empty": "", "set": "v" },
                    "Note": "kept",
                  },
                },
              },
            }
            """);

        await _service.SetAsync("shop", "Parameters:other", "x", secret: false, CancellationToken.None);

        var json = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        Assert.Equal("Warning", json.GetProperty("Logging").GetProperty("LogLevel").GetProperty("Default").GetString());
        var deploy = json.GetProperty("AspireDeploy");
        Assert.Equal(42, deploy.GetProperty("Custom").GetInt32());
        var shop = deploy.GetProperty("Profiles").GetProperty("shop");
        Assert.Equal("kept", shop.GetProperty("Note").GetString());
        Assert.True(shop.GetProperty("Source").GetProperty("Mine").GetBoolean());
        var parameters = shop.GetProperty("Parameters");
        Assert.Equal("", parameters.GetProperty("empty").GetString());
        Assert.Equal("x", parameters.GetProperty("other").GetString());
        Assert.False(parameters.TryGetProperty("absent", out _));
    }

    [Fact]
    public async Task Unparseable_file_is_reported_and_never_overwritten()
    {
        const string broken = "{ \"AspireDeploy\": { \"Profiles\": ";
        File.WriteAllText(_path, broken);

        Assert.NotNull(_service.Location.ParseError);
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _service.SaveAsync("shop", ValidProfile(_dir.Path), overwrite: true, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Equal(broken, File.ReadAllText(_path));
    }

    [Fact]
    public async Task Concurrent_changes_to_different_keys_both_persist()
    {
        await _service.SaveAsync("shop", ValidProfile(_dir.Path), overwrite: false, CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            Task.Run(() => _service.SetAsync("shop", $"Parameters:p{i}", $"v{i}", secret: false, CancellationToken.None))));

        var profile = await _service.GetAsync("shop", CancellationToken.None);
        Assert.Equal(10, profile!.Parameters.Count);
    }

    [Fact]
    public async Task Existing_profile_needs_overwrite()
    {
        await _service.SaveAsync("shop", ValidProfile(_dir.Path), overwrite: false, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _service.SaveAsync("shop", ValidProfile(_dir.Path), overwrite: false, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        await _service.SaveAsync("shop", ValidProfile(_dir.Path), overwrite: true, CancellationToken.None);
    }

    [Theory]
    [InlineData("Shop")]
    [InlineData("-shop")]
    [InlineData("s")]
    [InlineData("shop_1")]
    public async Task Bad_names_are_rejected(string name)
    {
        await Assert.ThrowsAsync<DeployerException>(() =>
            _service.SaveAsync(name, ValidProfile(_dir.Path), overwrite: false, CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_values_are_listed()
    {
        var bad = ValidProfile("relative\\path");
        bad.Destination.SubscriptionId = "not-a-guid";
        bad.Destination.ResourceGroup = "rg.";
        bad.ContainerRuntime = "lxc";
        var ex = await Assert.ThrowsAsync<DeployerException>(() => _service.SaveAsync("shop", bad, false, CancellationToken.None));
        Assert.Contains("Source:Location", ex.Message);
        Assert.Contains("Destination:SubscriptionId", ex.Message);
        Assert.Contains("Destination:ResourceGroup", ex.Message);
        Assert.Contains("ContainerRuntime", ex.Message);
    }

    [Fact]
    public async Task Set_rejects_unknown_keys_and_marks_secrets()
    {
        await _service.SaveAsync("shop", ValidProfile(_dir.Path), overwrite: false, CancellationToken.None);
        await Assert.ThrowsAsync<DeployerException>(() => _service.SetAsync("shop", "Destination:Nope", "x", false, CancellationToken.None));
        await Assert.ThrowsAsync<DeployerException>(() => _service.SetAsync("shop", "LastDeployedAspireVersion", "1", false, CancellationToken.None));

        await _service.SetAsync("shop", "Parameters:apiKey", "s3cr3t", secret: true, CancellationToken.None);
        var profile = await _service.GetAsync("shop", CancellationToken.None);
        Assert.Equal("s3cr3t", profile!.Parameters["apiKey"]);
        Assert.Contains("apiKey", profile.SecretParameters);

        await _service.SetAsync("shop", "Parameters:apiKey", null, secret: false, CancellationToken.None);
        profile = await _service.GetAsync("shop", CancellationToken.None);
        Assert.False(profile!.Parameters.ContainsKey("apiKey"));
    }

    [Fact]
    public async Task Delete_removes_only_that_profile_and_optionally_the_work_folder()
    {
        SeedWorkRoot();
        await _service.SaveAsync("shop", ValidProfile(_dir.Path), false, CancellationToken.None);
        await _service.SaveAsync("orders", ValidProfile(_dir.Path), false, CancellationToken.None);
        var workFolder = _dir.Combine("w", "shop");
        Directory.CreateDirectory(Path.Combine(workFolder, "logs"));

        await _service.DeleteAsync("shop", deleteWorkFolder: true, CancellationToken.None);

        Assert.Null(await _service.GetAsync("shop", CancellationToken.None));
        Assert.NotNull(await _service.GetAsync("orders", CancellationToken.None));
        Assert.False(Directory.Exists(workFolder));
    }

    [Fact]
    public async Task Delete_of_a_missing_or_locked_profile_is_code_2()
    {
        SeedWorkRoot();
        var missing = await Assert.ThrowsAsync<DeployerException>(() => _service.DeleteAsync("nope", false, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, missing.Code);

        await _service.SaveAsync("shop", ValidProfile(_dir.Path), false, CancellationToken.None);
        await using (Core.Infrastructure.WorkKeyLock.Acquire(_dir.Combine("w", "shop"), "shop"))
        {
            var locked = await Assert.ThrowsAsync<DeployerException>(() => _service.DeleteAsync("shop", false, CancellationToken.None));
            Assert.Equal(OutcomeCode.InvalidInput, locked.Code);
        }
        Assert.NotNull(await _service.GetAsync("shop", CancellationToken.None));
    }

    [Fact]
    public async Task Deployed_version_is_recorded()
    {
        await _service.SaveAsync("shop", ValidProfile(_dir.Path), false, CancellationToken.None);
        await _service.RecordDeployedVersionAsync("shop", "13.6.1", CancellationToken.None);
        Assert.Equal("13.6.1", (await _service.GetAsync("shop", CancellationToken.None))!.LastDeployedAspireVersion);
    }

    [Fact]
    public async Task Options_come_from_the_file_with_defaults()
    {
        File.WriteAllText(_path, """{ "AspireDeploy": { "WorkRoot": "D:\\w", "DeployTimeoutMinutes": 5, "GitHub": { "Token": "tok" } } }""");
        var options = (await _store.LoadAsync(CancellationToken.None)).Options;
        Assert.Equal("D:\\w", options.WorkRoot);
        Assert.Equal(5, options.DeployTimeoutMinutes);
        Assert.Equal(10, options.DiscoveryTimeoutMinutes);
        Assert.Equal("tok", options.GitHubToken);
    }
}

/// <summary>A locator pinned to one file.</summary>
internal sealed class FixedLocator(string path) : ISettingsLocator
{
    public SettingsLocation Resolve() => new(path, SettingsRule.EnvironmentVariable, File.Exists(path));
}
