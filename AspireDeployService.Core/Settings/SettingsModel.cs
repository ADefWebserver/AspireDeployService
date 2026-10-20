using System.Text.Json;
using System.Text.Json.Serialization;
using AspireDeployService.Core.Sources;

namespace AspireDeployService.Core.Settings;

/// <summary>
/// The whole <c>appsettings.Local.json</c> (implementation plan D8, source plan §6.3). Every level keeps
/// unknown keys in an extension-data bag, so a round trip never drops what a developer added by hand.
/// </summary>
public sealed class SettingsDocument
{
    public AspireDeploySection AspireDeploy { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AspireDeploySection
{
    public string? WorkRoot { get; set; }
    public string? ToolsRoot { get; set; }
    public string? MinimumAspireVersion { get; set; }
    public int? MaxTestedAspireMajor { get; set; }
    public int? DiscoveryTimeoutMinutes { get; set; }
    public int? DeployTimeoutMinutes { get; set; }
    public int? MaxSourceDownloadMegabytes { get; set; }
    public GitHubSection? GitHub { get; set; }
    public AISection? AI { get; set; }
    public Dictionary<string, DeployProfile> Profiles { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GitHubSection
{
    public string? Token { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// <c>AspireDeploy:AI</c> (AI Help plan §6.1). Provider keys are <c>AIServiceType</c> names; empty or absent strings
/// mean "not set".
/// </summary>
public sealed class AISection
{
    public string? ActiveServiceType { get; set; }

    // System.Text.Json replaces this dictionary on read, so the comparer is lost. AISettingsService maps each key
    // with AIServiceTypes.TryParse and skips (but keeps on save) keys that do not parse.
    public Dictionary<string, AIProviderSection> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool? EnableFileTools { get; set; }
    public int? MaxLogTailLines { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AIProviderSection
{
    public string? ApiKey { get; set; }
    public string? AIModel { get; set; }
    public string? Endpoint { get; set; }
    public string? ApiVersion { get; set; }
    public string? DeploymentPath { get; set; }
    public string? ApiProtocol { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One deploy profile (source plan §6.3 profile object, plus <see cref="LastDeployedAspireVersion"/> from D18).</summary>
public sealed class DeployProfile
{
    public ProfileSource Source { get; set; } = new();
    public ProfileDestination Destination { get; set; } = new();
    public string? Environment { get; set; }
    public string? ContainerRuntime { get; set; }

    /// <summary>A present "" value means "deliberately not supplied"; an absent key means missing.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ParameterConfigKeys { get; set; } = new(StringComparer.Ordinal);
    public List<string> SecretParameters { get; set; } = [];

    /// <summary>The AppHost SDK version of the last successful deploy (D18). Written only by the deploy service.</summary>
    public string? LastDeployedAspireVersion { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public string EnvironmentName => string.IsNullOrWhiteSpace(Environment) ? "Production" : Environment;

    public SourceSpec ToSourceSpec(string? refOverride = null) =>
        new(Source.Kind, Source.Location, refOverride ?? Source.Ref, Source.AppHostPath);

    public DeployProfile Clone() =>
        JsonSerializer.Deserialize<DeployProfile>(JsonSerializer.Serialize(this, SettingsJson.Options), SettingsJson.Options)!;
}

public sealed class ProfileSource
{
    public SourceKind Kind { get; set; }
    public string Location { get; set; } = "";
    public string? Ref { get; set; }
    public string? AppHostPath { get; set; }
    public string? GitHubToken { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProfileDestination
{
    public string? TenantId { get; set; }
    public string? SubscriptionId { get; set; }
    public string? Location { get; set; }
    public string? ResourceGroup { get; set; }
    public bool? AllowResourceGroupCreation { get; set; }
    public string? CredentialSource { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public static class SettingsJson
{
    /// <summary>Reads comments and trailing commas (hand edits); comments are not written back (D16).</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}
