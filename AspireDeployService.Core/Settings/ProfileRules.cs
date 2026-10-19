using System.Text.RegularExpressions;
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
        return errors;
    }

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
            case "ContainerRuntime": p.ContainerRuntime = NullIfEmpty(value); break;
            default:
                throw new DeployerException(OutcomeCode.InvalidInput, $"'{key}' is not a key that can be set.");
        }
    }

    private static void RequireName(string key, string name)
    {
        if (name.Length == 0)
            throw new DeployerException(OutcomeCode.InvalidInput, $"'{key}' needs a parameter name.");
    }
}
