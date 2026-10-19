using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Settings;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests;

public class AspireVersionRulesTests
{
    [Theory]
    [InlineData("12.9.0", false)]
    [InlineData("13.0.0", true)]
    [InlineData("13.6.1-preview.1.26506.6", true)]
    [InlineData("14.0.0", true)]
    public void Minimum_is_13_0(string v, bool supported)
    {
        Assert.True(AspireVersionRules.TryParse(v, out var parsed));
        Assert.Equal(supported, AspireVersionRules.IsSupported(parsed, new Version(13, 0, 0)));
    }

    [Fact]
    public void Version_14_is_beyond_tested() =>
        Assert.True(AspireVersionRules.IsBeyondTested(new Version(14, 0), 13));

    [Theory]
    [InlineData("13.1.0", ".aspire/settings.json")]
    [InlineData("13.2.0", "aspire.config.json")]
    public void Settings_file_moves_at_13_2(string v, string expected)
    {
        AspireVersionRules.TryParse(v, out var parsed);
        Assert.Equal(expected, AspireVersionRules.SettingsFileFor(parsed));
    }

    [Theory]
    [InlineData("13.3.0", new[] { "apphost.ts" })]
    [InlineData("13.4.0", new[] { "apphost.mts", "apphost.ts" })]
    public void TypeScript_name_changes_at_13_4(string v, string[] expected)
    {
        AspireVersionRules.TryParse(v, out var parsed);
        Assert.Equal(expected, AspireVersionRules.TypeScriptAppHostNames(parsed));
    }
}

public class AppHostScannerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static readonly AspireDeployOptions Options = AspireDeployOptions.From(null);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Project_sdk_attribute_with_Azure_target()
    {
        AppHostFixtures.ProjectAppHost(_dir, "src/Shop.AppHost", "13.6.1");
        var scan = AppHostScanner.Scan(_dir.Path, null, Options);

        Assert.Equal("src/Shop.AppHost/Shop.AppHost.csproj", scan.AppHost.AppHostPath);
        Assert.Equal(AppHostKind.ProjectCsproj, scan.AppHost.Kind);
        Assert.Equal("13.6.1", scan.AppHost.AspireSdkVersion);
        Assert.Contains("Aspire.Hosting.Azure.AppContainers", scan.AppHost.HostingPackages);
        Assert.Equal([DeploymentTarget.AzureContainerApps], scan.AppHost.DeclaredTargets);
        Assert.Empty(scan.Warnings);
    }

    [Theory]
    [InlineData("9.5.2")]
    [InlineData("8.2.0")]
    public void Older_versions_are_refused_with_code_6(string version)
    {
        AppHostFixtures.ProjectAppHost(_dir, "Old.AppHost", version);
        var ex = Assert.Throws<DeployerException>(() => AppHostScanner.Scan(_dir.Path, null, Options));
        Assert.Equal(OutcomeCode.UnsupportedVersion, ex.Code);
        Assert.Contains("aspire.dev", ex.Message);
    }

    [Fact]
    public void Sdk_element_form_of_13_is_accepted()
    {
        _dir.Write("A/A.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Sdk Name="Aspire.AppHost.Sdk" Version="13.1.0" />
              <PropertyGroup><IsAspireHost>true</IsAspireHost></PropertyGroup>
            </Project>
            """);
        _dir.Write("A/Program.cs", "builder.AddAzureAppServiceEnvironment(\"app\");");
        var scan = AppHostScanner.Scan(_dir.Path, null, Options);
        Assert.Equal("13.1.0", scan.AppHost.AspireSdkVersion);
        Assert.Equal([DeploymentTarget.AzureAppService], scan.AppHost.DeclaredTargets);
    }

    [Fact]
    public void Bare_sdk_uses_global_json_msbuild_sdks()
    {
        _dir.Write("global.json", """{ "msbuild-sdks": { "Aspire.AppHost.Sdk": "13.2.0" } }""");
        _dir.Write("H/H.csproj", """<Project Sdk="Aspire.AppHost.Sdk"><PropertyGroup /></Project>""");
        _dir.Write("H/AppHost.cs", AppHostFixtures.AcaAppHostCs);
        Assert.Equal("13.2.0", AppHostScanner.Scan(_dir.Path, null, Options).AppHost.AspireSdkVersion);
    }

    [Fact]
    public void Central_package_management_supplies_the_version()
    {
        _dir.Write("Directory.Packages.props", """
            <Project><ItemGroup><PackageVersion Include="Aspire.Hosting.AppHost" Version="13.0.2" /></ItemGroup></Project>
            """);
        _dir.Write("H/H.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsAspireHost>true</IsAspireHost></PropertyGroup>
              <ItemGroup><PackageReference Include="Aspire.Hosting.AppHost" /></ItemGroup>
            </Project>
            """);
        _dir.Write("H/Program.cs", AppHostFixtures.AcaAppHostCs);
        Assert.Equal("13.0.2", AppHostScanner.Scan(_dir.Path, null, Options).AppHost.AspireSdkVersion);
    }

    [Fact]
    public void File_based_apphost()
    {
        _dir.Write("apphost.cs", """
            #:sdk Aspire.AppHost.Sdk@13.6.1
            #:package Aspire.Hosting.Azure.AppContainers@13.6.1
            var builder = DistributedApplication.CreateBuilder(args);
            builder.AddAzureContainerAppEnvironment("env");
            """);
        var scan = AppHostScanner.Scan(_dir.Path, null, Options);
        Assert.Equal(AppHostKind.FileBasedCs, scan.AppHost.Kind);
        Assert.Equal("13.6.1", scan.AppHost.AspireSdkVersion);
        Assert.Contains("Aspire.Hosting.Azure.AppContainers", scan.AppHost.HostingPackages);
    }

    [Fact]
    public void TypeScript_apphost_reads_sdk_version_from_aspire_config()
    {
        _dir.Write("aspire.config.json", """
            { "appHost": { "path": "apphost.mts", "language": "typescript/nodejs" }, "sdk": { "version": "13.6.1" },
              "packages": { "Aspire.Hosting.Azure.AppContainers": "13.6.1" } }
            """);
        _dir.Write("package.json", "{}");
        _dir.Write("apphost.mts", "const builder = await createBuilder(); await builder.addAzureContainerAppEnvironment('env');");
        var scan = AppHostScanner.Scan(_dir.Path, null, Options);
        Assert.Equal(AppHostKind.TypeScript, scan.AppHost.Kind);
        Assert.Equal("apphost.mts", scan.AppHost.AppHostPath);
        Assert.Equal("13.6.1", scan.AppHost.AspireSdkVersion);
        Assert.True(scan.AppHost.HasAzureTarget);
    }

    [Fact]
    public void Legacy_aspire_settings_json_of_13_1()
    {
        AppHostFixtures.ProjectAppHost(_dir, "S.AppHost", "13.1.0");
        AppHostFixtures.ProjectAppHost(_dir, "Other.AppHost", "13.1.0");
        _dir.Write(".aspire/settings.json", """{ "appHostPath": "../S.AppHost/S.AppHost.csproj" }""");
        Assert.Equal("S.AppHost/S.AppHost.csproj", AppHostScanner.Scan(_dir.Path, null, Options).AppHost.AppHostPath);
    }

    [Fact]
    public void Two_apphosts_give_code_2_with_the_list()
    {
        AppHostFixtures.ProjectAppHost(_dir, "A.AppHost", "13.6.1");
        AppHostFixtures.ProjectAppHost(_dir, "B.AppHost", "13.6.1");
        var ex = Assert.Throws<DeployerException>(() => AppHostScanner.Scan(_dir.Path, null, Options));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Contains("A.AppHost/A.AppHost.csproj", ex.Message);
        Assert.Contains("B.AppHost/B.AppHost.csproj", ex.Message);
    }

    [Fact]
    public void Explicit_path_picks_one_of_several()
    {
        AppHostFixtures.ProjectAppHost(_dir, "A.AppHost", "13.6.1");
        AppHostFixtures.ProjectAppHost(_dir, "B.AppHost", "13.5.0");
        var scan = AppHostScanner.Scan(_dir.Path, "B.AppHost/B.AppHost.csproj", Options);
        Assert.Equal("13.5.0", scan.AppHost.AspireSdkVersion);
    }

    [Fact]
    public void No_apphost_gives_code_2()
    {
        _dir.Write("README.md", "nothing here");
        Assert.Equal(OutcomeCode.InvalidInput, Assert.Throws<DeployerException>(() => AppHostScanner.Scan(_dir.Path, null, Options)).Code);
    }

    [Fact]
    public void Skipped_folders_are_ignored()
    {
        AppHostFixtures.ProjectAppHost(_dir, "Real.AppHost", "13.6.1");
        foreach (var skipped in new[] { "bin", "obj", "node_modules", ".git", ".aspire/modules" })
            AppHostFixtures.ProjectAppHost(_dir, $"{skipped}/Copy.AppHost", "13.6.1");
        Assert.Equal("Real.AppHost/Real.AppHost.csproj", AppHostScanner.Scan(_dir.Path, null, Options).AppHost.AppHostPath);
    }

    [Fact]
    public void Submodules_and_lfs_pointers_are_warnings()
    {
        AppHostFixtures.ProjectAppHost(_dir, "A.AppHost", "13.6.1");
        _dir.Write(".gitmodules", "[submodule \"x\"]");
        _dir.Write("assets/logo.png", "version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 12\n");
        var scan = AppHostScanner.Scan(_dir.Path, null, Options);
        Assert.True(scan.UsesSubmodulesOrLfs);
        Assert.Contains(scan.Warnings, w => w.Contains("submodules"));
        Assert.Contains(scan.Warnings, w => w.Contains("LFS"));
    }

    [Fact]
    public void Several_targets_are_all_reported_and_no_azure_target_is_a_warning()
    {
        AppHostFixtures.ProjectAppHost(_dir, "A.AppHost", "13.6.1",
            "builder.AddDockerComposeEnvironment(\"c\"); builder.AddKubernetesEnvironment(\"k\");");
        File.WriteAllText(_dir.Combine("A.AppHost", "A.AppHost.csproj"),
            """<Project Sdk="Aspire.AppHost.Sdk/13.6.1"><PropertyGroup /></Project>""");
        var scan = AppHostScanner.Scan(_dir.Path, null, Options);
        Assert.Equal([DeploymentTarget.DockerCompose, DeploymentTarget.Kubernetes], scan.AppHost.DeclaredTargets);
        Assert.False(scan.AppHost.HasAzureTarget);
        Assert.Contains(scan.Warnings, w => w.Contains("No Azure compute environment"));
    }

    [Fact]
    public void This_repository_scans_as_13_5_4_with_no_azure_target()
    {
        var scan = AppHostScanner.Scan(AppHostFixtures.RepositoryRoot(), null, Options);
        Assert.Equal("AspireDeployService.AppHost/AspireDeployService.AppHost.csproj", scan.AppHost.AppHostPath);
        Assert.Equal(AppHostKind.ProjectCsproj, scan.AppHost.Kind);
        Assert.Equal("13.5.4", scan.AppHost.AspireSdkVersion);
        Assert.False(scan.AppHost.HasAzureTarget);
        Assert.Contains(scan.Warnings, w => w.Contains("No Azure compute environment"));
    }
}

public class ManifestParserTests
{
    [Fact]
    public void Starter_fixture_from_aspire_13_6_1()
    {
        var m = ManifestParser.Parse(File.ReadAllText(AppHostFixtures.Fixture("manifests", "starter-13.6.1.json")));

        var p = m.Parameters.ToDictionary(x => x.Name);
        Assert.True(p["apiKey"].Secret);
        Assert.True(p["apiKey"].NeedsValue);
        Assert.Equal("Parameters:apiKey", p["apiKey"].ConfigKey);
        Assert.False(p["region-name"].Secret);
        Assert.True(p["gen-pass"].Generated);
        Assert.False(p["gen-pass"].NeedsValue);
        Assert.Equal("ConnectionStrings:db", p["db"].ConfigKey);

        var r = m.Resources.ToDictionary(x => x.Name);
        Assert.True(r["env"].IsAzure);
        Assert.True(r["apiservice"].NeedsContainerRuntime);
        Assert.Equal("redis:7.4", r["cache"].Image);
        Assert.False(r["cache"].NeedsContainerRuntime);
        Assert.Empty(m.Warnings);
    }

    [Fact]
    public void Defaults_dockerfiles_executables_and_unknown_types()
    {
        var m = ManifestParser.Parse("""
            { "resources": {
              "p": { "type": "parameter.v0", "inputs": { "value": { "type": "string", "default": { "value": "x" } } } },
              "built": { "type": "container.v1", "build": { "context": "." } },
              "df": { "type": "dockerfile.v0", "path": "Dockerfile" },
              "exe": { "type": "executable.v0" },
              "future": { "type": "something.v9" } } }
            """);
        Assert.True(m.Parameters.Single().HasDefault);
        Assert.True(m.Resources.Single(r => r.Name == "built").NeedsContainerRuntime);
        Assert.True(m.Resources.Single(r => r.Name == "df").NeedsContainerRuntime);
        Assert.Contains(m.Warnings, w => w.Contains("not deployed"));
        Assert.Contains(m.Warnings, w => w.Contains("unknown resource type"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{ \"x\": 1 }")]
    public void Malformed_manifest_is_code_4(string json) =>
        Assert.Equal(OutcomeCode.DiscoveryFailed, Assert.Throws<DeployerException>(() => ManifestParser.Parse(json)).Code);
}
