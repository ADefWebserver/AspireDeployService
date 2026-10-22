using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Settings;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests.Deploy;

/// <summary>Resource pickers plan D4: the per-target locks and the environment names.</summary>
public class AppHostScannerLockTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private AppHostInfo Scan(string code, string? extraFile = null)
    {
        AppHostFixtures.ProjectAppHost(_dir, "src/Shop.AppHost", "13.6.1", code);
        if (extraFile is not null)
            _dir.Write("src/Shop.AppHost/Extensions/More.cs", extraFile);
        return AppHostScanner.Scan(_dir.Path, null, AspireDeployOptions.From(new AspireDeploySection())).AppHost;
    }

    [Fact]
    public void Plain_starter_is_not_locked()
    {
        var info = Scan(AppHostFixtures.AcaAppHostCs);
        Assert.False(info.EnvironmentLocked);
        Assert.False(info.RegistryLocked);
        Assert.False(info.IdentityLocked);
        Assert.Equal(["env"], info.ContainerAppEnvironmentNames);
    }

    [Fact]
    public void Environment_marked_existing_across_lines_is_locked()
    {
        var info = Scan("""
            var builder = DistributedApplication.CreateBuilder(args);
            var name = builder.AddParameter("envName");
            builder.AddAzureContainerAppEnvironment("env")
                .PublishAsExisting(name, null);
            builder.Build().Run();
            """);
        Assert.True(info.EnvironmentLocked);
        Assert.False(info.RegistryLocked);
    }

    [Fact]
    public void An_existing_key_vault_alone_does_not_lock_the_environment()
    {
        var info = Scan("""
            var builder = DistributedApplication.CreateBuilder(args);
            builder.AddAzureContainerAppEnvironment("env");
            builder.AddAzureKeyVault("kv").RunAsExisting("kv", "rg");
            builder.Build().Run();
            """);
        Assert.False(info.EnvironmentLocked);
    }

    [Fact]
    public void Registry_and_identity_locks_and_names_from_every_project_file()
    {
        var info = Scan(AppHostFixtures.AcaAppHostCs, """
            static class More
            {
                public static void Add(IDistributedApplicationBuilder b)
                {
                    b.AddAzureContainerAppEnvironment("second").WithAzureContainerRegistry(acr).WithAcrPullIdentity(id);
                }
            }
            """);
        Assert.True(info.RegistryLocked);
        Assert.True(info.IdentityLocked);
        Assert.Equal(["env", "second"], info.ContainerAppEnvironmentNames.OrderBy(n => n));
    }

    [Fact]
    public void A_sibling_file_in_the_project_does_not_count()
    {
        var info = Scan(AppHostFixtures.AcaAppHostCs);
        _dir.Write("src/Shop.AppHost/.ads-deploy/AppHost.cs", "builder.AddAzureContainerAppEnvironment(\"zzz\").WithAzureContainerRegistry(x);");
        info = AppHostScanner.Scan(_dir.Path, null, AspireDeployOptions.From(new AspireDeploySection())).AppHost;
        Assert.False(info.RegistryLocked);
        Assert.Equal(["env"], info.ContainerAppEnvironmentNames);
    }
}
