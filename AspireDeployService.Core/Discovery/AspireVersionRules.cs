namespace AspireDeployService.Core.Discovery;

/// <summary>The few things that change between Aspire 13.x versions (source plan §6.4).</summary>
public static class AspireVersionRules
{
    public const string UpgradeLink = "https://aspire.dev/"; // the upgrade guides live here

    /// <summary>Parses "13.6.1", "13.6.1-preview.1.2" or "13.6" into a <see cref="Version"/>.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var core = text.Trim().Split('-', '+')[0];
        if (!Version.TryParse(core, out var parsed))
            return false;
        version = parsed;
        return true;
    }

    public static bool IsSupported(Version v, Version minimum) => v >= minimum;

    public static bool IsBeyondTested(Version v, int maxMajor) => v.Major > maxMajor;

    /// <summary>Below 13.2: ".aspire/settings.json"; otherwise "aspire.config.json".</summary>
    public static string SettingsFileFor(Version v) =>
        v < new Version(13, 2) ? ".aspire/settings.json" : "aspire.config.json";

    /// <summary>Below 13.4: ["apphost.ts"]; otherwise ["apphost.mts", "apphost.ts"].</summary>
    public static string[] TypeScriptAppHostNames(Version v) =>
        v < new Version(13, 4) ? ["apphost.ts"] : ["apphost.mts", "apphost.ts"];

    public static string UnsupportedMessage(string found) =>
        $"This AppHost uses Aspire {found}. Only Aspire 13.0 and later can be deployed. Upgrade it first: {UpgradeLink}";
}
