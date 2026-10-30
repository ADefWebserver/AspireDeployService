using System.Reflection;

namespace AspireDeployService.Services;

/// <summary>
/// The build this app is: ApplicationDisplayVersion plus the commit the SDK stamps into the informational version
/// ("1.0+e11e1be0f7…"). The commit is missing when the build ran outside a git checkout.
/// </summary>
public static class AppVersion
{
    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>"1.0".</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>The full commit hash, or null.</summary>
    public static string? Commit => Informational.Split('+') is [_, var sha, ..] && sha.Length > 0 ? sha : null;

    /// <summary>"v1.0 · e11e1be", for the top bar.</summary>
    public static string Short => Commit is { } c ? $"v{Version} · {c[..Math.Min(7, c.Length)]}" : $"v{Version}";

    /// <summary>"Version 1.0, commit e11e1be0f7…", for a tooltip.</summary>
    public static string Full => Commit is { } c ? $"Version {Version}, commit {c}" : $"Version {Version}";
}
