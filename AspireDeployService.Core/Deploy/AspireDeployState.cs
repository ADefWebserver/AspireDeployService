using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AspireDeployService.Core.Deploy;

/// <summary>
/// Aspire's own deploy-state cache, <c>~/.aspire/deployments/&lt;SHA-256 of the lowercased AppHost path&gt;/&lt;environment&gt;.json</c>,
/// plus a <c>.json.migration</c> snapshot (<c>CurrentState</c>, <c>ClaimedSections</c>) that Aspire restores from.
/// Found in the resource pickers end-to-end test: a value cached there wins over the <c>Azure__*</c> and
/// <c>Parameters__*</c> environment variables. The cache is per AppHost, not per profile, so two profiles over one
/// AppHost (or a profile whose registry or environment changed) would deploy with the other's values. This class removes
/// just the cached entries that would be wrong; nothing else is touched.
/// </summary>
public static class AspireDeployState
{
    private static readonly string[] OverlayPrefixes = ["Parameters:adsExisting", "Parameters:adsOverlay"];

    /// <summary>Test seam: the folder that holds Aspire's per-AppHost state folders.</summary>
    internal static string? RootOverride { get; set; }

    public static string Root => RootOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "deployments");

    public static string FolderFor(string appHostFullPath) =>
        Path.Combine(Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(appHostFullPath).ToLowerInvariant()))));

    /// <summary>Removes the overlay's cached parameters for one AppHost and environment. Returns the keys removed.</summary>
    public static IReadOnlyList<string> RemoveOverlayParameters(string appHostFullPath, string environmentName) =>
        Scrub(appHostFullPath, environmentName, key => OverlayPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// When the cache was written for another subscription, resource group or region, removes its whole <c>Azure</c>
    /// section (destination and the cached Azure deployments), so Aspire deploys to the profile's destination and
    /// provisions there. Returns the cached destination that was removed, or null when the cache matches or is absent.
    /// </summary>
    public static string? ResetForOtherDestination(string appHostFullPath, string environmentName,
        string subscriptionId, string resourceGroup, string location)
    {
        var file = Path.Combine(FolderFor(appHostFullPath), environmentName.ToLowerInvariant() + ".json");
        if (Read(file) is not { } doc)
            return null;
        var cachedSub = doc["Azure:SubscriptionId"]?.GetValue<string>();
        var cachedGroup = doc["Azure:ResourceGroup"]?.GetValue<string>();
        var cachedLocation = doc["Azure:Location"]?.GetValue<string>();
        bool Differs(string? cached, string wanted) => cached is not null && !string.Equals(cached, wanted, StringComparison.OrdinalIgnoreCase);
        if (!Differs(cachedSub, subscriptionId) && !Differs(cachedGroup, resourceGroup) && !Differs(cachedLocation, location))
            return null;
        Scrub(appHostFullPath, environmentName, key => key == "Azure" || key.StartsWith("Azure:", StringComparison.OrdinalIgnoreCase));
        return $"{cachedGroup ?? "?"} in {cachedLocation ?? "?"} (subscription {cachedSub ?? "?"})";
    }

    /// <summary>Removes matching top-level keys from the state file and its migration snapshot. Returns the keys removed.</summary>
    private static IReadOnlyList<string> Scrub(string appHostFullPath, string environmentName, Func<string, bool> matches)
    {
        var removed = new List<string>();
        var folder = FolderFor(appHostFullPath);
        if (!Directory.Exists(folder))
            return removed;
        var name = environmentName.ToLowerInvariant() + ".json";
        foreach (var file in new[] { Path.Combine(folder, name), Path.Combine(folder, name + ".migration") })
        {
            if (Read(file) is not { } doc)
                continue;
            var keys = doc.Select(p => p.Key).Where(matches).ToList();
            foreach (var key in keys)
                doc.Remove(key);
            var changed = keys.Count > 0;

            // CurrentState is a JSON string with nested sections ({"Parameters":{"x":...},"Azure":{...}}).
            if (doc["CurrentState"]?.GetValue<string>() is { } state && JsonNode.Parse(state) is JsonObject current)
            {
                foreach (var section in current.Select(p => p.Key).ToList())
                {
                    if (matches(section))
                    {
                        current.Remove(section);
                        keys.Add(section);
                        changed = true;
                    }
                    else if (current[section] is JsonObject nested)
                    {
                        foreach (var child in nested.Select(p => p.Key).Where(c => matches(section + ":" + c)).ToList())
                        {
                            nested.Remove(child);
                            keys.Add(section + ":" + child);
                            changed = true;
                        }
                    }
                }
                doc["CurrentState"] = current.ToJsonString();
            }

            // ClaimedSections is a JSON string array of section keys.
            if (doc["ClaimedSections"]?.GetValue<string>() is { } claimed && JsonNode.Parse(claimed) is JsonArray sections)
            {
                var kept = new JsonArray(sections.Where(s => s?.GetValue<string>() is not { } v || !matches(v)).Select(s => s?.DeepClone()).ToArray());
                if (kept.Count != sections.Count)
                {
                    doc["ClaimedSections"] = kept.ToJsonString();
                    changed = true;
                }
            }

            if (!changed)
                continue;
            File.WriteAllText(file, doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            removed.AddRange(keys.Where(k => !removed.Contains(k)));
        }
        return removed;
    }

    private static JsonObject? Read(string file)
    {
        if (!File.Exists(file))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
