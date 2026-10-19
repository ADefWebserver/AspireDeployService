namespace AspireDeployService.Tests.Support;

/// <summary>Writes AppHost layouts into a temp folder (plan §11 scanner fixtures, built at run time).</summary>
public static class AppHostFixtures
{
    public const string AcaAppHostCs = """
        var builder = DistributedApplication.CreateBuilder(args);
        builder.AddAzureContainerAppEnvironment("env");
        builder.Build().Run();
        """;

    public static void ProjectAppHost(TempDir dir, string folder, string sdkVersion, string code = AcaAppHostCs, string extraItems = "")
    {
        dir.Write($"{folder}/{Path.GetFileName(folder)}.csproj", $"""
            <Project Sdk="Aspire.AppHost.Sdk/{sdkVersion}">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.Azure.AppContainers" Version="{sdkVersion}" />
                {extraItems}
              </ItemGroup>
            </Project>
            """);
        dir.Write($"{folder}/AppHost.cs", code);
    }

    /// <summary>The source repository's root folder, found by walking up to the .slnx.</summary>
    public static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "AspireDeployService.slnx")))
                return d.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    public static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);
}
