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

    /// <summary>Resource pickers plan D4: the AppHost already marks its Container Apps environment as existing.</summary>
    public bool EnvironmentLocked { get; init; }

    /// <summary>Resource pickers plan D4: the AppHost already wires its own container registry.</summary>
    public bool RegistryLocked { get; init; }

    /// <summary>Resource pickers plan §6.4: the AppHost already sets its own ACR pull identity.</summary>
    public bool IdentityLocked { get; init; }

    /// <summary>
    /// The AppHost source calls <c>AddAzureContainerAppEnvironment(</c>. The package reference alone puts Container Apps
    /// in <see cref="DeclaredTargets"/>, but the overlay needs the call to find an environment.
    /// </summary>
    public bool CallsAddContainerAppEnvironment { get; init; }

    /// <summary>The names passed to <c>AddAzureContainerAppEnvironment("...")</c>, in source order.</summary>
    public IReadOnlyList<string> ContainerAppEnvironmentNames { get; init; } = [];
}

/// <param name="ConfigKey">"Parameters:&lt;name&gt;" or "ConnectionStrings:&lt;name&gt;" (source plan D12).</param>
public sealed record DiscoveredParameter(string Name, bool Secret, bool HasDefault, bool Generated, string ConfigKey)
{
    /// <summary>A value must come from the profile (or be marked "AppHost supplies it").</summary>
    [JsonIgnore]
    public bool NeedsValue => !HasDefault && !Generated;
}

public sealed record DiscoveredResource(string Name, string Type, string? Image, string? ProjectPath, bool IsAzure, bool NeedsContainerRuntime)
{
    /// <summary>
    /// The container app name from the resource's generated Bicep module, when the AppHost renames the app (for example
    /// <c>PublishAsAzureContainerApp((_, app) =&gt; app.Name = "web")</c>); null when the module was not read.
    /// </summary>
    public string? ContainerAppName { get; init; }

    /// <summary>The name of the container app Aspire deploys for this resource: the Bicep name, else the resource name lowercased.</summary>
    [JsonIgnore]
    public string AppName => ContainerAppName ?? Name.ToLowerInvariant();
}

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
