namespace AspireDeployService.Core.Settings;

/// <summary>The resolved <c>AspireDeploy:*</c> settings with defaults (source plan §6.3, plan §6.3).</summary>
public sealed record AspireDeployOptions
{
    public required string WorkRoot { get; init; }
    public required string ToolsRoot { get; init; }
    public Version MinimumAspireVersion { get; init; } = new(13, 0, 0);
    public int MaxTestedAspireMajor { get; init; } = 13;
    public int DiscoveryTimeoutMinutes { get; init; } = 10;
    public int DeployTimeoutMinutes { get; init; } = 60;
    public int MaxSourceDownloadMegabytes { get; init; } = 512;
    public string? GitHubToken { get; init; }

    public static string DefaultWorkRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ads", "w");

    public static string DefaultToolsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ads", "tools");

    public static AspireDeployOptions From(AspireDeploySection? s) => new()
    {
        WorkRoot = string.IsNullOrWhiteSpace(s?.WorkRoot) ? DefaultWorkRoot : Environment.ExpandEnvironmentVariables(s.WorkRoot),
        ToolsRoot = string.IsNullOrWhiteSpace(s?.ToolsRoot) ? DefaultToolsRoot : Environment.ExpandEnvironmentVariables(s.ToolsRoot),
        MinimumAspireVersion = Version.TryParse(s?.MinimumAspireVersion, out var min) ? min : new Version(13, 0, 0),
        MaxTestedAspireMajor = s?.MaxTestedAspireMajor ?? 13,
        DiscoveryTimeoutMinutes = s?.DiscoveryTimeoutMinutes is > 0 ? s.DiscoveryTimeoutMinutes.Value : 10,
        DeployTimeoutMinutes = s?.DeployTimeoutMinutes is > 0 ? s.DeployTimeoutMinutes.Value : 60,
        MaxSourceDownloadMegabytes = s?.MaxSourceDownloadMegabytes is > 0 ? s.MaxSourceDownloadMegabytes.Value : 512,
        GitHubToken = string.IsNullOrWhiteSpace(s?.GitHub?.Token) ? null : s.GitHub.Token
    };
}
