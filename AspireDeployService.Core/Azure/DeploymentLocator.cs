using AspireDeployService.Core.Deploy;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Runs;

// Rank is adapted from BlazorDataDeploymentWebsite.Web/Services/Deployments/InstallationDiscoveryService.cs
// (GroupCandidates; wizard plan D16): grouped by (resource group, environment) instead of environment only, keyed on the
// AppHost's compute names instead of BlazorData roles, with Found/Missing, origin and PinEnvironment added.
// NameFromId and ResourceGroupFromId are copied verbatim from the same file.
namespace AspireDeployService.Core.Azure;

public enum DeploymentOrigin { AspireDeploy, Azd, Unknown }

/// <param name="EnvironmentId">null when the matched apps report no environment.</param>
/// <param name="EnvironmentResourceGroup">The environment's own group (it may differ from ResourceGroup).</param>
/// <param name="Location">The environment's region, else the first matched app's region, else the group's.</param>
/// <param name="Found">Lowercased compute names found in this group and environment.</param>
/// <param name="Missing">Compute names not found there (they will be created).</param>
/// <param name="PinEnvironment">Wizard plan D10: Use this also sets the environment to Use existing.</param>
/// <param name="Warnings">D10 warnings, ready for the UI.</param>
public sealed record DeploymentCandidate(
    string SubscriptionId, string ResourceGroup, string Location,
    string? EnvironmentId, string? EnvironmentName, string? EnvironmentResourceGroup,
    IReadOnlyList<string> Found, IReadOnlyList<string> Missing,
    DeploymentOrigin Origin, bool PinEnvironment, IReadOnlyList<string> Warnings);

/// <summary>Finds existing deployments of an AppHost by its container app names (wizard plan F2b, §6.5).</summary>
public static class DeploymentLocator
{
    public const int MaxCandidates = 20;

    public const string AzdWarning =
        "Created by azd with its own names. Add .WithAzdResourceNaming() to AddAzureContainerAppEnvironment in the AppHost, "
        + "or the deploy will likely be refused.";
    public const string NamingWarning =
        "This environment was not named by Aspire for this AppHost, so the profile will use it as an existing environment.";
    public const string MixedWarning = "This group has apps in several environments.";
    public const string NoEnvironmentWarning = "Could not read the apps' environment.";

    /// <summary>
    /// Pure. Groups apps by (ResourceGroup, EnvironmentId) and keeps groups with at least one found name, ordered by
    /// Found.Count descending, then resource group name. Returns at most <see cref="MaxCandidates"/> candidates.
    /// </summary>
    /// <param name="computeNames">The AppHost's compute resources (<see cref="ContainerAppPlanner.IsCompute"/>), lowercased.</param>
    /// <param name="appHostEnvironmentNames"><see cref="AppHostInfo.ContainerAppEnvironmentNames"/>.</param>
    public static IReadOnlyList<DeploymentCandidate> Rank(
        string subscriptionId,
        IReadOnlyList<string> computeNames,
        IReadOnlyList<string> appHostEnvironmentNames,
        IReadOnlyList<AzureContainerApp> apps,
        IReadOnlyList<AzureContainerAppEnvironment> environments,
        IReadOnlyList<AzureResourceGroup> groups)
    {
        var wanted = computeNames.Select(n => n.ToLowerInvariant()).Distinct().ToList();
        var envPrefixes = appHostEnvironmentNames.Select(AlphaNumeric).Where(p => p.Length > 0).ToList();
        var matched = apps.Where(a => wanted.Contains(a.Name.ToLowerInvariant())).ToList();

        var list = new List<DeploymentCandidate>();
        foreach (var byGroup in matched.GroupBy(a => a.ResourceGroup, StringComparer.OrdinalIgnoreCase))
        {
            var byEnvironment = byGroup.GroupBy(a => a.EnvironmentId ?? "", StringComparer.OrdinalIgnoreCase).ToList();
            var mixed = byEnvironment.Count > 1;
            var group = groups.FirstOrDefault(g => Same(g.Name, byGroup.Key));
            var origin = OriginOf(group);

            foreach (var envGroup in byEnvironment)
            {
                var envId = envGroup.Key.Length > 0 ? envGroup.Key : null;
                var env = envId is null ? null : environments.FirstOrDefault(e => Same(e.Id, envId));
                var envName = env?.Name ?? (envId is null ? null : NameFromId(envId));
                var envGroupName = env?.ResourceGroup ?? (envId is null ? null : ResourceGroupFromId(envId));

                var found = wanted.Where(n => envGroup.Any(a => Same(a.Name, n))).ToList();
                var missing = wanted.Except(found).ToList();
                var warnings = new List<string>();
                var pin = false;

                if (envName is null)
                {
                    warnings.Add(NoEnvironmentWarning);
                }
                else
                {
                    var namedByAspire = envPrefixes.Count == 0
                        || envPrefixes.Any(p => AlphaNumeric(envName).StartsWith(p, StringComparison.Ordinal));
                    if (!namedByAspire)
                    {
                        if (origin == DeploymentOrigin.Azd)
                            warnings.Add(AzdWarning);
                        warnings.Add(NamingWarning);
                        pin = true;
                    }
                    if (mixed)
                    {
                        warnings.Add(MixedWarning);
                        pin = true;
                    }
                }

                var location = Region(env?.Location) ?? Region(envGroup.FirstOrDefault(a => a.Location is { Length: > 0 })?.Location)
                    ?? Region(group?.Location) ?? "";
                list.Add(new DeploymentCandidate(subscriptionId, group?.Name ?? byGroup.Key, location,
                    envId, envName, envGroupName, found, missing, origin, pin, warnings));
            }
        }

        return list
            .OrderByDescending(c => c.Found.Count)
            .ThenBy(c => c.ResourceGroup, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToList();
    }

    /// <summary>Reads apps (subscription-wide), environments and groups with refresh, then calls <see cref="Rank"/>.</summary>
    public static async Task<IReadOnlyList<DeploymentCandidate>> FindAsync(
        IAzureAccountService azure, string subscriptionId, DiscoveryResult discovery, IRunObserver observer, CancellationToken ct)
    {
        var compute = ComputeNames(discovery);
        if (compute.Count == 0)
            throw new DeployerException(OutcomeCode.InvalidInput, "This AppHost has no compute resources to look for.");

        var apps = await azure.ListContainerAppsAsync(subscriptionId, null, observer, ct, refresh: true);
        var environments = await azure.ListContainerAppEnvironmentsAsync(subscriptionId, observer, ct, refresh: true);
        var groups = await azure.ListResourceGroupsAsync(subscriptionId, observer, ct, refresh: true);
        return Rank(subscriptionId, compute, discovery.AppHost.ContainerAppEnvironmentNames, apps, environments, groups);
    }

    /// <summary>The container app names Aspire gives the AppHost's compute resources (<see cref="DiscoveredResource.AppName"/>), lowercased.</summary>
    public static IReadOnlyList<string> ComputeNames(DiscoveryResult discovery) =>
        discovery.Resources.Where(ContainerAppPlanner.IsCompute).Select(r => r.AppName.ToLowerInvariant()).Distinct().ToList();

    /// <summary>
    /// One short line for a failed search (after BlazorDataDeploymentWebsite's ArmErrors.Describe, keyed on
    /// <see cref="AzErrorKind"/>); anything else is the az error as reported.
    /// </summary>
    public static string DescribeFailure(string message) => AzCli.Classify(message) switch
    {
        AzErrorKind.Unauthorized => "Your Azure account cannot read this subscription. You need at least Reader access.",
        AzErrorKind.NotSignedIn => "The Azure CLI is not signed in, or its sign-in expired.",
        _ => message
    };

    /// <summary>Wizard plan D10: origin from resource group tags only.</summary>
    internal static DeploymentOrigin OriginOf(AzureResourceGroup? group)
    {
        if (group?.Tags is not { } tags)
            return DeploymentOrigin.Unknown;
        if (tags.ContainsKey("azd-env-name"))
            return DeploymentOrigin.Azd;
        if (tags.TryGetValue("aspire", out var aspire) && string.Equals(aspire, "true", StringComparison.OrdinalIgnoreCase))
            return DeploymentOrigin.AspireDeploy;
        return DeploymentOrigin.Unknown;
    }

    internal static string NameFromId(string resourceId) => resourceId.TrimEnd('/').Split('/').Last();

    internal static string? ResourceGroupFromId(string resourceId)
    {
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p.Equals("resourceGroups", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : null;
    }

    /// <summary>"East US 2" (az containerapp env list) and "eastus2" both become "eastus2"; null when empty.</summary>
    private static string? Region(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string AlphaNumeric(string value) => new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
