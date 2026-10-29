using System.Text.RegularExpressions;
using AspireDeployService.Core.Azure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Sources;

namespace AspireDeployService.Core.Settings;

/// <summary>Profile name and value validation, and the settable keys (source plan §6.1 <c>profile set</c>).</summary>
public static partial class ProfileRules
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[-\w\.\(\)]{1,90}$")]
    private static partial Regex ResourceGroupPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoPattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static void EnsureValidName(string name)
    {
        if (!IsValidName(name))
            throw new DeployerException(OutcomeCode.InvalidInput,
                $"'{name}' is not a valid profile name: use 2 to 40 lower-case letters, digits or dashes, starting with a letter or digit.");
    }

    /// <summary>Every problem with a profile, as "Key: message". Empty when the profile can be deployed.</summary>
    public static IReadOnlyList<string> Validate(DeployProfile p)
    {
        var errors = new List<string>();
        switch (p.Source.Kind)
        {
            case SourceKind.LocalFolder when string.IsNullOrWhiteSpace(p.Source.Location) || !Path.IsPathFullyQualified(p.Source.Location):
                errors.Add("Source:Location: enter the absolute path of the solution folder.");
                break;
            case SourceKind.GitHub when !RepoPattern().IsMatch(p.Source.Location ?? ""):
                errors.Add("Source:Location: enter the repository as <owner>/<repo>.");
                break;
        }
        if (p.Source.AppHostPath is { } appHost && (Path.IsPathRooted(appHost) || appHost.Split('/', '\\').Contains("..")))
            errors.Add("Source:AppHostPath: must be relative to the source root, without '..'.");

        if (!Guid.TryParse(p.Destination.SubscriptionId, out _))
            errors.Add("Destination:SubscriptionId: enter the subscription GUID.");
        if (p.Destination.TenantId is { Length: > 0 } tenant && !Guid.TryParse(tenant, out _))
            errors.Add("Destination:TenantId: must be a GUID or empty.");
        if (string.IsNullOrWhiteSpace(p.Destination.Location))
            errors.Add("Destination:Location: enter an Azure region, for example eastus2.");
        if (!IsValidResourceGroup(p.Destination.ResourceGroup))
            errors.Add("Destination:ResourceGroup: 1 to 90 letters, digits, '-', '_', '.', '(' or ')', not ending with '.'.");
        foreach (var (name, key) in p.ParameterConfigKeys)
        {
            if (Deploy.DeployEnvironment.IsReserved(Deploy.DeployEnvironment.ToEnvironmentVariable(key)))
                errors.Add($"ParameterConfigKeys:{name}: '{key}' is reserved for the deployment itself (Azure:*, AZURE_*, ASPIRE_*, DOTNET_*, PATH, ...).");
        }
        if (p.ContainerRuntime is { Length: > 0 } rt && rt is not ("docker" or "podman"))
            errors.Add("ContainerRuntime: must be docker, podman or empty.");
        foreach (var name in p.Parameters.Keys.Concat(p.ParameterConfigKeys.Keys).Distinct(StringComparer.Ordinal))
        {
            if (IsReservedParameterName(name))
                errors.Add($"Parameters:{name}: names starting with adsExisting or adsOverlay are reserved by Aspire Deploy.");
        }
        ValidateResources(p.Destination, errors);
        return errors;
    }

    /// <summary>Lowercases the registry name: Azure keeps the casing it was created with, but names are case-insensitive.</summary>
    public static void Normalize(DeployProfile p)
    {
        if (p.Destination.ContainerRegistry is { Name: { } name } r)
            r.Name = name.Trim().ToLowerInvariant();
    }

    /// <summary>Resource pickers plan D12: the overlay's own parameters.</summary>
    public static bool IsReservedParameterName(string name) =>
        name.StartsWith("adsExisting", StringComparison.OrdinalIgnoreCase) || name.StartsWith("adsOverlay", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resource pickers plan §6.1 rules for the registry, environment and pull-identity sections.</summary>
    private static void ValidateResources(ProfileDestination d, List<string> errors)
    {
        const string reg = "Destination:ContainerRegistry";
        const string env = "Destination:ContainerAppEnvironment";
        const string id = "Destination:AcrPullIdentity";

        if (d.ContainerRegistry is { } r && r.EffectiveMode != ResourceMode.AspireDefault)
        {
            if (string.IsNullOrWhiteSpace(r.Name))
                errors.Add($"{reg}:Name: choose or enter a registry.");
            else if (AzureNameRules.Problem(AzureResourceKind.ContainerRegistry, r.Name) is { } problem)
                errors.Add($"{reg}:Name: {problem}");
            if (r.EffectiveMode == ResourceMode.Existing && r.ResourceGroup is { Length: > 0 } rg && !IsValidResourceGroup(rg))
                errors.Add($"{reg}:ResourceGroup: {AzureNameRules.ResourceGroupRule}");
            if (r.EffectiveMode == ResourceMode.New && r.Sku is { Length: > 0 } sku && !ResourceChoice.Skus.Contains(sku, StringComparer.Ordinal))
                errors.Add($"{reg}:Sku: Basic, Standard or Premium.");
        }

        if (d.ContainerAppEnvironment is { } e)
        {
            if (e.AppHostResource is { Length: > 0 } res && !AppHostResourcePattern().IsMatch(res))
                errors.Add($"{env}:AppHostResource: 1 to 64 letters, digits or dashes.");
            if (e.EffectiveMode != ResourceMode.AspireDefault)
            {
                if (string.IsNullOrWhiteSpace(e.Name))
                    errors.Add($"{env}:Name: choose or enter an environment.");
                // Azure keeps an existing environment's name as it was created, capitals included, and matches it
                // case-insensitively; only a new name must be lowercase.
                else if (AzureNameRules.Problem(AzureResourceKind.ContainerAppEnvironment,
                             e.EffectiveMode == ResourceMode.Existing ? e.Name.ToLowerInvariant() : e.Name) is { } problem)
                    errors.Add($"{env}:Name: {problem}");
                if (e.EffectiveMode == ResourceMode.Existing && e.ResourceGroup is { Length: > 0 } rg && !IsValidResourceGroup(rg))
                    errors.Add($"{env}:ResourceGroup: {AzureNameRules.ResourceGroupRule}");
            }
            if (e.EffectiveMode == ResourceMode.New)
            {
                if (e.LogsDestination is { Length: > 0 } dest && !ResourceChoice.LogsDestinations.Contains(dest, StringComparer.Ordinal))
                    errors.Add($"{env}:LogsDestination: log-analytics, existing-workspace or none.");
                else if (e.EffectiveLogsDestination == ResourceChoice.LogsExistingWorkspace
                         && (string.IsNullOrWhiteSpace(e.LogsWorkspace?.Name) || !IsValidResourceGroup(e.LogsWorkspace?.ResourceGroup)))
                    errors.Add($"{env}:LogsWorkspace: choose a Log Analytics workspace.");
            }
        }

        if (d.AcrPullIdentity is { } i && i.EffectiveMode != ResourceMode.AspireDefault)
        {
            if (i.EffectiveMode == ResourceMode.New)
            {
                errors.Add($"{id}:Mode: only an existing identity can be used.");
                return;
            }
            if (string.IsNullOrWhiteSpace(i.Name) || !IsValidResourceGroup(i.ResourceGroup))
                errors.Add($"{id}:Name: choose an identity.");
            if (d.RegistryMode != ResourceMode.Existing)
                errors.Add($"{id}: an existing pull identity needs an existing registry that it can already pull from.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    internal static partial Regex AppHostResourcePattern();

    public static bool IsValidResourceGroup(string? name) =>
        name is not null && ResourceGroupPattern().IsMatch(name) && !name.EndsWith('.');

    /// <summary>
    /// Applies one <c>profile set</c> change. A null value removes the key (for a parameter: absent = missing);
    /// "" keeps an empty value ("deliberately not supplied"). Unknown keys give code 2.
    /// </summary>
    public static void ApplySet(DeployProfile p, string key, string? value, bool secret)
    {
        static string? NullIfEmpty(string? v) => string.IsNullOrEmpty(v) ? null : v;

        if (key.StartsWith("Parameters:", StringComparison.Ordinal))
        {
            var name = key["Parameters:".Length..];
            RequireName(key, name);
            if (value is null)
                p.Parameters.Remove(name);
            else
                p.Parameters[name] = value;
            if (secret && !p.SecretParameters.Contains(name))
                p.SecretParameters.Add(name);
            return;
        }
        if (key.StartsWith("ParameterConfigKeys:", StringComparison.Ordinal))
        {
            var name = key["ParameterConfigKeys:".Length..];
            RequireName(key, name);
            if (string.IsNullOrEmpty(value))
                p.ParameterConfigKeys.Remove(name);
            else
                p.ParameterConfigKeys[name] = value;
            return;
        }

        switch (key)
        {
            case "Source:Ref": p.Source.Ref = NullIfEmpty(value); break;
            case "Source:AppHostPath": p.Source.AppHostPath = NullIfEmpty(value); break;
            case "Source:GitHubToken": p.Source.GitHubToken = NullIfEmpty(value); break;
            case "Destination:TenantId": p.Destination.TenantId = NullIfEmpty(value); break;
            case "Destination:SubscriptionId": p.Destination.SubscriptionId = NullIfEmpty(value); break;
            case "Destination:Location": p.Destination.Location = NullIfEmpty(value); break;
            case "Destination:ResourceGroup": p.Destination.ResourceGroup = NullIfEmpty(value); break;
            case "Destination:CredentialSource": p.Destination.CredentialSource = NullIfEmpty(value); break;
            case "Destination:AllowResourceGroupCreation":
                p.Destination.AllowResourceGroupCreation = value switch
                {
                    null or "" => null,
                    _ when bool.TryParse(value, out var b) => b,
                    _ => throw new DeployerException(OutcomeCode.InvalidInput, $"{key} must be true or false.")
                };
                break;
            case "Environment": p.Environment = NullIfEmpty(value); break;
            case "Destination:ContainerRegistry:Mode": Choice(p, Section.Registry).Mode = ParseMode(key, value); break;
            case "Destination:ContainerRegistry:Name": Choice(p, Section.Registry).Name = NullIfEmpty(value?.Trim().ToLowerInvariant()); break;
            case "Destination:ContainerRegistry:ResourceGroup": Choice(p, Section.Registry).ResourceGroup = NullIfEmpty(value); break;
            case "Destination:ContainerRegistry:Sku": Choice(p, Section.Registry).Sku = NullIfEmpty(value); break;
            case "Destination:ContainerAppEnvironment:Mode": Choice(p, Section.Environment).Mode = ParseMode(key, value); break;
            case "Destination:ContainerAppEnvironment:Name": Choice(p, Section.Environment).Name = NullIfEmpty(value); break;
            case "Destination:ContainerAppEnvironment:ResourceGroup": Choice(p, Section.Environment).ResourceGroup = NullIfEmpty(value); break;
            case "Destination:ContainerAppEnvironment:LogsDestination": Choice(p, Section.Environment).LogsDestination = NullIfEmpty(value); break;
            case "Destination:ContainerAppEnvironment:AppHostResource": Choice(p, Section.Environment).AppHostResource = NullIfEmpty(value); break;
            case "Destination:ContainerAppEnvironment:LogsWorkspace:Name":
                (Choice(p, Section.Environment).LogsWorkspace ??= new()).Name = NullIfEmpty(value);
                break;
            case "Destination:ContainerAppEnvironment:LogsWorkspace:ResourceGroup":
                (Choice(p, Section.Environment).LogsWorkspace ??= new()).ResourceGroup = NullIfEmpty(value);
                break;
            case "Destination:AcrPullIdentity:Mode": Choice(p, Section.Identity).Mode = ParseMode(key, value); break;
            case "Destination:AcrPullIdentity:Name": Choice(p, Section.Identity).Name = NullIfEmpty(value); break;
            case "Destination:AcrPullIdentity:ResourceGroup": Choice(p, Section.Identity).ResourceGroup = NullIfEmpty(value); break;
            case "ContainerRuntime": p.ContainerRuntime = NullIfEmpty(value); break;
            default:
                throw new DeployerException(OutcomeCode.InvalidInput, $"'{key}' is not a key that can be set.");
        }
    }

    private enum Section { Registry, Environment, Identity }

    private static ResourceChoice Choice(DeployProfile p, Section section) => section switch
    {
        Section.Registry => p.Destination.ContainerRegistry ??= new(),
        Section.Environment => p.Destination.ContainerAppEnvironment ??= new(),
        _ => p.Destination.AcrPullIdentity ??= new()
    };

    /// <summary>The three enum names, case-insensitively; AspireDefault and "" both store nothing.</summary>
    private static ResourceMode? ParseMode(string key, string? value) => value switch
    {
        null or "" => null,
        _ when !int.TryParse(value, out _) && Enum.TryParse<ResourceMode>(value, ignoreCase: true, out var mode) =>
            mode == ResourceMode.AspireDefault ? null : mode,
        _ => throw new DeployerException(OutcomeCode.InvalidInput, $"{key} must be AspireDefault, Existing or New.")
    };

    private static void RequireName(string key, string name)
    {
        if (name.Length == 0)
            throw new DeployerException(OutcomeCode.InvalidInput, $"'{key}' needs a parameter name.");
    }
}
