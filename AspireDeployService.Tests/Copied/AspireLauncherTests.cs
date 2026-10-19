// Copied from BlazorDataDeploymentWebsite.Tests/Worker/ToolInstallerTests.cs (implementation plan D2).
using AspireDeployService.Core.Tools;

namespace AspireDeployService.Tests.Copied;

/// <summary>Finding the Aspire CLI that <c>dotnet tool install --tool-path</c> produced.</summary>
public class AspireLauncherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bdn-aspire-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_launcher_is_not_installed() => Assert.Null(AspireLauncher.FindAspire(_dir));

    [Fact]
    public void Finds_the_platform_launcher()
    {
        var name = OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
        File.WriteAllText(Path.Combine(_dir, name), "");

        Assert.Equal(Path.Combine(_dir, name), AspireLauncher.FindAspire(_dir));
    }

    [Fact]
    public void Accepts_the_cmd_shim_on_Windows()
    {
        if (!OperatingSystem.IsWindows())
            return; // aspire.cmd is only produced on Windows
        File.WriteAllText(Path.Combine(_dir, "aspire.cmd"), "@echo off");

        Assert.Equal(Path.Combine(_dir, "aspire.cmd"), AspireLauncher.FindAspire(_dir));
    }
}
