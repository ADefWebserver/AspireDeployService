using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using AspireDeployService.Core.Errors;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Core.Azure;

/// <param name="State">The subscription state from <c>az account list</c>, for example Enabled, Warned or Disabled.</param>
/// <param name="User">The signed-in user or service principal that sees it (<c>user.name</c>), for the top bar.</param>
public sealed record AzureSubscription(string Id, string Name, string TenantId, bool IsDefault, string State = "Enabled", string? User = null)
{
    /// <summary>A subscription that can take a deployment (the same rule as BlazorDataDeploymentWebsite).</summary>
    public bool IsUsable => State is "Enabled" or "Warned" or "PastDue";
}

/// <param name="Name">The region name a deploy uses, for example <c>eastus2</c>.</param>
/// <param name="SupportsContainerApps">Whether Microsoft.App offers managed environments there; null when that could not be read.</param>
public sealed record AzureLocation(string Name, string DisplayName, string? Geography, bool? SupportsContainerApps);

/// <param name="Tags">The group's string tags (wizard plan D10); null when it has none.</param>
public sealed record AzureResourceGroup(string Name, string Location, IReadOnlyDictionary<string, string>? Tags = null);

// Resource pickers plan §6.3.
public sealed record AzureContainerRegistry(string Name, string ResourceGroup, string Location, string LoginServer, string Sku);
public sealed record AzureContainerAppEnvironment(string Id, string Name, string ResourceGroup, string Location);
public sealed record AzureContainerApp(string Name, string ResourceGroup, string? EnvironmentId, string? Location = null);
public sealed record NameAvailability(bool Available, string? Reason);
public sealed record AzureManagedIdentity(string Id, string Name, string ResourceGroup, string PrincipalId);
public sealed record AzureLogAnalyticsWorkspace(string Id, string Name, string ResourceGroup, string Location);

public interface IAzureAccountService
{
    Task<IReadOnlyList<AzureContainerRegistry>> ListContainerRegistriesAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false);

    Task<IReadOnlyList<AzureContainerAppEnvironment>> ListContainerAppEnvironmentsAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false);

    /// <summary>
    /// <paramref name="resourceGroup"/> null = every app in the subscription. A resource group that does not exist yet
    /// gives an empty list, not an error.
    /// </summary>
    Task<IReadOnlyList<AzureContainerApp>> ListContainerAppsAsync(string subscriptionId, string? resourceGroup, IRunObserver observer, CancellationToken ct, bool refresh = false);

    /// <summary>Never cached.</summary>
    Task<NameAvailability> CheckRegistryNameAsync(string subscriptionId, string name, IRunObserver observer, CancellationToken ct);

    Task<IReadOnlyList<AzureManagedIdentity>> ListManagedIdentitiesAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false);

    Task<IReadOnlyList<AzureLogAnalyticsWorkspace>> ListLogAnalyticsWorkspacesAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false);

    /// <summary>Drops every cached list for that subscription; called after a create.</summary>
    void Invalidate(string subscriptionId);

    /// <summary>Drops every cached list, including subscriptions; called after a sign-in.</summary>
    void InvalidateAll();

    /// <summary>The subscriptions visible to the developer's <c>az login</c>. Code 3 when not signed in.</summary>
    Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(IRunObserver observer, CancellationToken ct, bool refresh = false);

    /// <summary>The physical regions of a subscription, marked with Container Apps support.</summary>
    Task<IReadOnlyList<AzureLocation>> ListLocationsAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false);

    /// <summary>The resource groups in a subscription, sorted by name.</summary>
    Task<IReadOnlyList<AzureResourceGroup>> ListResourceGroupsAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false);
}

/// <summary>
/// Reads Azure through the developer's own <c>az login</c>. Results are kept for <see cref="CacheLifetime"/>, so the
/// profile pages can cascade (subscription → regions and resource groups) without re-running the CLI each time.
/// </summary>
public sealed partial class AzureAccountService(ProfileStore store, IRunContextFactory runs, TimeProvider time) : IAzureAccountService
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, object Value)> _cache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z0-9]{1,40}$")]
    private static partial Regex LocationNamePattern();

    public Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(IRunObserver observer, CancellationToken ct, bool refresh = false) =>
        CachedAsync("subscriptions", refresh, async () =>
        {
            var json = await RunAzAsync(["account", "list", "--output", "json"], observer, ct);
            var list = ParseSubscriptions(json);
            if (list.Count == 0)
                // A signed-out az (2.6x) also answers [] with exit code 0, so this covers both cases.
                throw new DeployerException(OutcomeCode.MissingPrerequisite, "The Azure CLI is not signed in, or it sees no subscriptions. Run: az login",
                    ErrorCategory.Permissions);
            return list;
        });

    public Task<IReadOnlyList<AzureLocation>> ListLocationsAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false) =>
        CachedAsync("locations:" + RequireGuid(subscriptionId), refresh, async () =>
        {
            // az account list-locations has no --subscription, so read ARM directly (BlazorDataDeploymentWebsite's call).
            var locations = await RunAzAsync(["rest", "--method", "get", "--url",
                $"https://management.azure.com/subscriptions/{subscriptionId}/locations?api-version=2022-12-01", "--output", "json"], observer, ct);
            string? provider = null;
            try
            {
                provider = await RunAzAsync(["provider", "show", "--namespace", "Microsoft.App", "--subscription", subscriptionId, "--output", "json"], observer, ct);
            }
            catch (DeployerException)
            {
                // Support stays unknown; Aspire's own deploy still reports an unsupported region.
            }
            return ParseLocations(locations, provider);
        });

    public Task<IReadOnlyList<AzureResourceGroup>> ListResourceGroupsAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false) =>
        CachedAsync("groups:" + RequireGuid(subscriptionId), refresh, async () =>
            ParseResourceGroups(await RunAzAsync(["group", "list", "--subscription", subscriptionId, "--output", "json"], observer, ct)));

    public Task<IReadOnlyList<AzureContainerRegistry>> ListContainerRegistriesAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false) =>
        CachedAsync("registries:" + RequireGuid(subscriptionId), refresh, async () =>
            ParseRegistries(await RunAzAsync(["acr", "list", "--subscription", subscriptionId, "--output", "json"], observer, ct)));

    public Task<IReadOnlyList<AzureContainerAppEnvironment>> ListContainerAppEnvironmentsAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false) =>
        CachedAsync("environments:" + RequireGuid(subscriptionId), refresh, async () =>
            ParseEnvironments(await RunAzAsync(["containerapp", "env", "list", "--subscription", subscriptionId, "--output", "json"], observer, ct)));

    public Task<IReadOnlyList<AzureContainerApp>> ListContainerAppsAsync(string subscriptionId, string? resourceGroup, IRunObserver observer, CancellationToken ct, bool refresh = false)
    {
        if (resourceGroup is not null && !ProfileRules.IsValidResourceGroup(resourceGroup))
            throw new DeployerException(OutcomeCode.InvalidInput, $"'{resourceGroup}' is not a valid resource group name.");
        return CachedAsync($"apps:{RequireGuid(subscriptionId)}:{resourceGroup ?? "*"}", refresh, async () =>
        {
            List<string> args = ["containerapp", "list", "--subscription", subscriptionId];
            if (resourceGroup is not null)
                args.AddRange(["--resource-group", resourceGroup]);
            args.AddRange(["--output", "json"]);
            var result = await RunAsync(args, observer, ct);
            if (result.Kind == AzErrorKind.ResourceGroupNotFound)
                return [];
            return ParseContainerApps(Require(result, args));
        });
    }

    public async Task<NameAvailability> CheckRegistryNameAsync(string subscriptionId, string name, IRunObserver observer, CancellationToken ct) =>
        ParseNameAvailability(await RunAzAsync(["acr", "check-name", "--name", name, "--subscription", RequireGuid(subscriptionId), "--output", "json"], observer, ct));

    public Task<IReadOnlyList<AzureManagedIdentity>> ListManagedIdentitiesAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false) =>
        CachedAsync("identities:" + RequireGuid(subscriptionId), refresh, async () =>
            ParseIdentities(await RunAzAsync(["identity", "list", "--subscription", subscriptionId, "--output", "json"], observer, ct)));

    public Task<IReadOnlyList<AzureLogAnalyticsWorkspace>> ListLogAnalyticsWorkspacesAsync(string subscriptionId, IRunObserver observer, CancellationToken ct, bool refresh = false) =>
        CachedAsync("workspaces:" + RequireGuid(subscriptionId), refresh, async () =>
            ParseWorkspaces(await RunAzAsync(["monitor", "log-analytics", "workspace", "list", "--subscription", subscriptionId, "--output", "json"], observer, ct)));

    public void Invalidate(string subscriptionId)
    {
        foreach (var key in _cache.Keys)
        {
            if (key.Contains(':') && key[(key.IndexOf(':') + 1)..].StartsWith(subscriptionId, StringComparison.OrdinalIgnoreCase))
                _cache.TryRemove(key, out _);
        }
    }

    public void InvalidateAll() => _cache.Clear();

    internal static IReadOnlyList<AzureContainerRegistry> ParseRegistries(string json) => Parse(json, "az acr list", root =>
        root.EnumerateArray()
            .Select(r => new AzureContainerRegistry(Text(r, "name"), Text(r, "resourceGroup"), Text(r, "location"), Text(r, "loginServer"),
                r.TryGetProperty("sku", out var sku) && sku.ValueKind == JsonValueKind.Object ? Text(sku, "name") : ""))
            .Where(r => r.Name.Length > 0)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    internal static IReadOnlyList<AzureContainerAppEnvironment> ParseEnvironments(string json) => Parse(json, "az containerapp env list", root =>
        root.EnumerateArray()
            .Select(e => new AzureContainerAppEnvironment(Text(e, "id"), Text(e, "name"), Text(e, "resourceGroup"), Text(e, "location")))
            .Where(e => e.Name.Length > 0)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    /// <summary>Reads <c>properties.managedEnvironmentId</c>, or <c>properties.environmentId</c> on other az versions (R4).</summary>
    internal static IReadOnlyList<AzureContainerApp> ParseContainerApps(string json) => Parse(json, "az containerapp list", root =>
        root.EnumerateArray()
            .Select(a =>
            {
                string? envId = null;
                if (a.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                    envId = Text(props, "managedEnvironmentId") is { Length: > 0 } m ? m : Text(props, "environmentId") is { Length: > 0 } e ? e : null;
                envId ??= Text(a, "managedEnvironmentId") is { Length: > 0 } top ? top : null;
                return new AzureContainerApp(Text(a, "name"), Text(a, "resourceGroup"), envId,
                    Text(a, "location") is { Length: > 0 } location ? location : null);
            })
            .Where(a => a.Name.Length > 0)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    internal static NameAvailability ParseNameAvailability(string json) => Parse(json, "az acr check-name", root =>
    {
        var available = root.TryGetProperty("nameAvailable", out var a) && a.ValueKind == JsonValueKind.True;
        var reason = Text(root, "message") is { Length: > 0 } message ? message : Text(root, "reason") is { Length: > 0 } r ? r : null;
        return new NameAvailability(available, available ? null : reason);
    });

    internal static IReadOnlyList<AzureManagedIdentity> ParseIdentities(string json) => Parse(json, "az identity list", root =>
        root.EnumerateArray()
            .Select(i => new AzureManagedIdentity(Text(i, "id"), Text(i, "name"), Text(i, "resourceGroup"), Text(i, "principalId")))
            .Where(i => i.Name.Length > 0)
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    internal static IReadOnlyList<AzureLogAnalyticsWorkspace> ParseWorkspaces(string json) => Parse(json, "az monitor log-analytics workspace list", root =>
        root.EnumerateArray()
            .Select(w => new AzureLogAnalyticsWorkspace(Text(w, "id"), Text(w, "name"), Text(w, "resourceGroup"), Text(w, "location")))
            .Where(w => w.Name.Length > 0)
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    internal static IReadOnlyList<AzureSubscription> ParseSubscriptions(string json) => Parse(json, "az account list", root =>
        root.EnumerateArray()
            .Select(s => new AzureSubscription(
                Text(s, "id"), Text(s, "name"), Text(s, "tenantId"),
                s.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True,
                Text(s, "state") is { Length: > 0 } state ? state : "Enabled",
                s.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object && Text(u, "name") is { Length: > 0 } user ? user : null))
            .Where(s => s.Id.Length > 0)
            .OrderByDescending(s => s.IsDefault).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    /// <summary>Physical regions only (the same filter as BlazorDataDeploymentWebsite), sorted by display name.</summary>
    internal static IReadOnlyList<AzureLocation> ParseLocations(string json, string? microsoftAppProviderJson)
    {
        var supported = ContainerAppsRegions(microsoftAppProviderJson);
        return Parse(json, "the subscription's locations", root =>
            (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) ? value : root).EnumerateArray()
                .Where(l => !l.TryGetProperty("metadata", out var m) || Text(m, "regionType") is "" or "Physical")
                .Select(l =>
                {
                    var name = Text(l, "name");
                    var display = Text(l, "displayName") is { Length: > 0 } dn ? dn : name;
                    var geography = l.TryGetProperty("metadata", out var m) ? Text(m, "geographyGroup") : "";
                    bool? apps = supported is null ? null : supported.Contains(Squash(display)) || supported.Contains(Squash(name));
                    return new AzureLocation(name, display, geography.Length > 0 ? geography : null, apps);
                })
                .Where(l => LocationNamePattern().IsMatch(l.Name))
                .OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    internal static IReadOnlyList<AzureResourceGroup> ParseResourceGroups(string json) => Parse(json, "az group list", root =>
        root.EnumerateArray()
            .Select(g => new AzureResourceGroup(Text(g, "name"), Text(g, "location"), Tags(g)))
            .Where(g => g.Name.Length > 0)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    /// <summary>The string values of a <c>tags</c> object; null when absent, <c>null</c>, or empty.</summary>
    private static IReadOnlyDictionary<string, string>? Tags(JsonElement e)
    {
        if (!e.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return null;
        var map = tags.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
        return map.Count == 0 ? null : map;
    }

    /// <summary>Microsoft.App's managedEnvironments locations, squashed for comparison; null when unknown.</summary>
    private static HashSet<string>? ContainerAppsRegions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("resourceTypes", out var types))
                return null;
            foreach (var type in types.EnumerateArray())
            {
                if (!string.Equals(Text(type, "resourceType"), "managedEnvironments", StringComparison.OrdinalIgnoreCase)
                    || !type.TryGetProperty("locations", out var locations))
                    continue;
                return locations.EnumerateArray().Select(l => Squash(l.GetString() ?? "")).ToHashSet(StringComparer.Ordinal);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"East US 2" and "eastus2" both become "eastus2".</summary>
    private static string Squash(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static T Parse<T>(string json, string command, Func<JsonElement, T> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return read(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new DeployerException(OutcomeCode.MissingPrerequisite, $"Could not read the output of {command}.", inner: ex);
        }
    }

    private static string RequireGuid(string subscriptionId) =>
        Guid.TryParse(subscriptionId, out _)
            ? subscriptionId
            : throw new DeployerException(OutcomeCode.InvalidInput, "Choose a subscription first.");

    private async Task<IReadOnlyList<T>> CachedAsync<T>(string key, bool refresh, Func<Task<IReadOnlyList<T>>> load)
    {
        if (!refresh && _cache.TryGetValue(key, out var hit) && time.GetUtcNow() - hit.At < CacheLifetime)
            return (IReadOnlyList<T>)hit.Value;
        var value = await load();
        _cache[key] = (time.GetUtcNow(), value);
        return value;
    }

    private async Task<string> RunAzAsync(IReadOnlyList<string> args, IRunObserver observer, CancellationToken ct) =>
        Require(await RunAsync(args, observer, ct), args);

    /// <summary>Each list call keeps its own run context, as before the resource pickers plan.</summary>
    private async Task<AzResult> RunAsync(IReadOnlyList<string> args, IRunObserver observer, CancellationToken ct)
    {
        var options = (await store.LoadAsync(ct)).Options;
        await using var run = runs.Create(WorkKeys.For(options.WorkRoot, null, null), observer);
        return await AzCli.RunAsync(args, run, AzCli.DefaultTimeout, ct);
    }

    private static string Require(AzResult result, IReadOnlyList<string> args)
    {
        if (result.Succeeded)
            return result.StdOut;
        // Wizard plan D15: sign-in failures carry Permissions, so the editor can offer Sign in to Azure.
        if (args[0] == "account" && args[1] == "list")
            throw new DeployerException(OutcomeCode.MissingPrerequisite, "The Azure CLI is not signed in. Run: az login", ErrorCategory.Permissions);
        var message = $"{AzCli.Describe(args)} failed: {result.ErrorLine}";
        if (result.Kind == AzErrorKind.NotSignedIn)
            throw new DeployerException(OutcomeCode.MissingPrerequisite, message + "\nSign in to Azure again.", ErrorCategory.Permissions);
        if (result.Kind == AzErrorKind.ProviderNotRegistered)
            message += "\n" + AzCli.ProviderFix;
        throw new DeployerException(OutcomeCode.MissingPrerequisite, message);
    }
}
