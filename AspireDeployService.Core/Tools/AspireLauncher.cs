namespace AspireDeployService.Core.Tools;

public static class AspireLauncher
{
    /// <summary>
    /// The Aspire CLI launcher in a <c>dotnet tool install --tool-path</c> folder. On Windows the RID-specific
    /// aspire.cli package installs an <c>aspire.cmd</c> shim instead of <c>aspire.exe</c>; PATH lookup finds either.
    /// Copied from BlazorDataDeploymentWebsite.DeployWorker/Tools/ToolInstaller.cs (implementation plan D2).
    /// </summary>
    public static string? FindAspire(string aspireDir)
    {
        string[] names = OperatingSystem.IsWindows() ? ["aspire.exe", "aspire.cmd"] : ["aspire"];
        return names.Select(n => Path.Combine(aspireDir, n)).FirstOrDefault(File.Exists);
    }
}
