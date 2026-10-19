namespace AspireDeployService.Core.Settings;

public enum SettingsRule { EnvironmentVariable, ProjectFolder, LocalAppData }

/// <param name="ParseError">Set when the file exists but cannot be parsed; the app is then read-only (D16).</param>
public sealed record SettingsLocation(string Path, SettingsRule Rule, bool Exists, string? ParseError = null);

public interface ISettingsLocator
{
    /// <summary>Resolved on every call, so a file created or an environment variable set later is picked up.</summary>
    SettingsLocation Resolve();
}

/// <summary>
/// Implementation plan D7: (1) <c>ADS_SETTINGS</c>; (2) <c>appsettings.Local.json</c> in the nearest folder at or
/// above the app's base directory that contains <c>AspireDeployService.csproj</c>, whether or not the settings
/// file exists there yet; (3) <c>%LOCALAPPDATA%/AspireDeployService/appsettings.Local.json</c>.
/// </summary>
public sealed class SettingsLocator(Func<string, string?> getEnvironmentVariable, string baseDirectory, string localAppData) : ISettingsLocator
{
    public const string FileName = "appsettings.Local.json";
    public const string EnvironmentVariable = "ADS_SETTINGS";
    public const string ProjectFileName = "AspireDeployService.csproj";

    public SettingsLocator()
        : this(Environment.GetEnvironmentVariable, AppContext.BaseDirectory,
               Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    public SettingsLocation Resolve()
    {
        if (getEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            var full = Path.GetFullPath(fromEnv);
            return new SettingsLocation(full, SettingsRule.EnvironmentVariable, File.Exists(full));
        }

        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ProjectFileName)))
            {
                var path = Path.Combine(dir.FullName, FileName);
                return new SettingsLocation(path, SettingsRule.ProjectFolder, File.Exists(path));
            }
        }

        var appData = Path.Combine(localAppData, "AspireDeployService", FileName);
        return new SettingsLocation(appData, SettingsRule.LocalAppData, File.Exists(appData));
    }
}
