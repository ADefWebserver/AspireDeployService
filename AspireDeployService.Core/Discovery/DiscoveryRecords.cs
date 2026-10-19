using System.Text.Json;
using System.Text.Json.Serialization;
using AspireDeployService.Core.Sources;

namespace AspireDeployService.Core.Discovery;

[JsonConverter(typeof(JsonStringEnumConverter<AppHostKind>))]
public enum AppHostKind { ProjectCsproj, FileBasedCs, TypeScript }

[JsonConverter(typeof(JsonStringEnumConverter<DeploymentTarget>))]
public enum DeploymentTarget { AzureContainerApps, AzureAppService, AzureKubernetes, DockerCompose, Kubernetes }

public static class DeploymentTargets
{
    public static bool IsAzure(this DeploymentTarget t) =>
        t is DeploymentTarget.AzureContainerApps or DeploymentTarget.AzureAppService or DeploymentTarget.AzureKubernetes;
}

/// <param name="AppHostPath">Relative to the source root, with forward slashes.</param>
/// <param name="AspireSdkVersion">As written in the source, for example "13.6.1".</param>
public sealed record AppHostInfo(
    string AppHostPath,
    AppHostKind Kind,
    string AspireSdkVersion,
    IReadOnlyList<string> HostingPackages,
    IReadOnlyList<DeploymentTarget> DeclaredTargets,
    string? DotnetSdkFromGlobalJson)
{
    [JsonIgnore]
    public bool HasAzureTarget => DeclaredTargets.Any(t => t.IsAzure());
}

/// <param name="ConfigKey">"Parameters:&lt;name&gt;" or "ConnectionStrings:&lt;name&gt;" (source plan D12).</param>
public sealed record DiscoveredParameter(string Name, bool Secret, bool HasDefault, bool Generated, string ConfigKey)
{
    /// <summary>A value must come from the profile (or be marked "AppHost supplies it").</summary>
    [JsonIgnore]
    public bool NeedsValue => !HasDefault && !Generated;
}

public sealed record DiscoveredResource(string Name, string Type, string? Image, string? ProjectPath, bool IsAzure, bool NeedsContainerRuntime);

public sealed record DiscoveryResult(
    AppHostInfo AppHost,
    SourceIdentity Source,
    IReadOnlyList<DiscoveredParameter> Parameters,
    IReadOnlyList<DiscoveredResource> Resources,
    IReadOnlyList<string> Warnings,
    bool ManifestRan)
{
    /// <summary>Submodules or Git LFS pointers were found: profile init and deploy refuse (source plan §6.4).</summary>
    [JsonIgnore]
    public bool UsesSubmodulesOrLfs { get; init; }

    [JsonIgnore]
    public bool NeedsContainerRuntime => Resources.Any(r => r.NeedsContainerRuntime);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
