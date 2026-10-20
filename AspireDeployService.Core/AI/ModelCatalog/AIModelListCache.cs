using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AspireDeployService.Core.AI.Models;
using AspireDeployService.Core.AI.Providers;

namespace AspireDeployService.Core.AI.ModelCatalog;

/// <summary>
/// Keeps each successful model list in memory for 24 hours (plan D3, replacing the reference app's Table cache).
/// The key is the provider, the normalized endpoint and a short hash of the API key, so the key itself is never held.
/// </summary>
public sealed class AIModelListCache(AIProviderRegistry registry, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, (DateTimeOffset Stored, ModelListResult Result)> _entries = new();

    public async Task<ModelListResult> GetAsync(AIProviderSettings settings, bool refresh, CancellationToken ct)
    {
        if (registry.GetCatalog(settings.ServiceType) is not { } catalog)
            return ModelListResult.NotConfigured("No model list for this provider. Type the model name.");

        var key = CacheKey(settings);
        if (!refresh && _entries.TryGetValue(key, out var entry) && time.GetUtcNow() - entry.Stored < Lifetime)
            return entry.Result;

        var result = await catalog.ListModelsAsync(settings, ct);
        if (result.Status == ModelListStatus.Success)
            _entries[key] = (time.GetUtcNow(), result);
        else
            _entries.TryRemove(key, out _);
        return result;
    }

    internal static string CacheKey(AIProviderSettings settings)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey.Trim())))[..16];
        return $"{settings.ServiceType}|{AIKeyEndpointGuard.Normalize(settings.Endpoint)}|{hash}";
    }
}
