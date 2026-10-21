using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Core.Azure;

/// <param name="State">The subscription state from <c>az account list</c>, for example Enabled, Warned or Disabled.</param>
public sealed record AzureSubscription(string Id, string Name, string TenantId, bool IsDefault, string State = "Enabled")
{
    /// <summary>A subscription that can take a deployment (the same rule as BlazorDataDeploymentWebsite).</summary>
    public bool IsUsable => State is "Enabled" or "Warned" or "PastDue";
}

/// <param name="Name">The region name a deploy uses, for example <c>eastus2</c>.</param>
/// <param name="SupportsContainerApps">Whether Microsoft.App offers managed environments there; null when that could not be read.</param>
public sealed record AzureLocation(string Name, string DisplayName, string? Geography, bool? SupportsContainerApps);

public sealed record AzureResourceGroup(string Name, string Location);

public interface IAzureAccountService
{
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
                throw new DeployerException(OutcomeCode.MissingPrerequisite, "The Azure CLI is signed in but sees no subscriptions. Run: az login");
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

    internal static IReadOnlyList<AzureSubscription> ParseSubscriptions(string json) => Parse(json, "az account list", root =>
        root.EnumerateArray()
            .Select(s => new AzureSubscription(
                Text(s, "id"), Text(s, "name"), Text(s, "tenantId"),
                s.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True,
                Text(s, "state") is { Length: > 0 } state ? state : "Enabled"))
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
            .Select(g => new AzureResourceGroup(Text(g, "name"), Text(g, "location")))
            .Where(g => g.Name.Length > 0)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

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

    private async Task<string> RunAzAsync(IReadOnlyList<string> args, IRunObserver observer, CancellationToken ct)
    {
        var options = (await store.LoadAsync(ct)).Options;
        await using var run = runs.Create(WorkKeys.For(options.WorkRoot, null, null), observer);
        ProcessResult result;
        try
        {
            var spec = new ProcessSpec("az", args, Path.GetTempPath())
            {
                CaptureStdOut = true,
                LogStdOut = false,
                Timeout = TimeSpan.FromMinutes(2)
            };
            result = await run.Runner.RunAsync(spec, run.Log, ct);
        }
        catch (Win32Exception ex)
        {
            throw new DeployerException(OutcomeCode.MissingPrerequisite, "The Azure CLI (az) is not installed.", inner: ex);
        }

        if (!result.Succeeded)
            throw new DeployerException(OutcomeCode.MissingPrerequisite,
                args[0] == "account" && args[1] == "list"
                    ? "The Azure CLI is not signed in. Run: az login"
                    : $"az {string.Join(' ', args.Take(2))} failed: {LastLine(result)}");
        return string.Join('\n', result.StdOut);
    }

    /// <summary>The CLI's own "ERROR: ..." line when there is one; az prints help text after it.</summary>
    private static string LastLine(ProcessResult result)
    {
        var lines = result.OutputTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? "see the log";
    }
}
