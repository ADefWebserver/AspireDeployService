using AspireDeployService.Core.AI.Models;
using AspireDeployService.Core.AI.Providers;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Core.AI;

public interface IAISettingsService
{
    /// <summary>Raised after every successful write, on any thread.</summary>
    event Action? Changed;

    /// <summary>A fresh read of <c>AspireDeploy:AI</c>, never cached. Empty settings when the file does not parse.</summary>
    Task<AISettings> GetAsync(CancellationToken ct);

    Task<AIOptions> GetOptionsAsync(CancellationToken ct);
    Task SetOptionsAsync(AIOptions options, CancellationToken ct);

    /// <summary>Saves one provider. Code 2 when the endpoint is not https, or changed without the key typed again (D14).</summary>
    Task SaveProviderAsync(AIProviderEdit edit, bool makeActive, CancellationToken ct);

    Task SetActiveAsync(AIServiceType type, CancellationToken ct);

    /// <summary>Removes the whole provider entry.</summary>
    Task ClearProviderAsync(AIServiceType type, CancellationToken ct);
}

/// <summary>
/// AI settings over <see cref="ProfileStore"/> (plan D3): every write is the store's locked read-modify-write, so an
/// unparseable file is never overwritten and keys this class does not know survive a save.
/// </summary>
public sealed class AISettingsService(ProfileStore store) : IAISettingsService
{
    public event Action? Changed;

    public async Task<AISettings> GetAsync(CancellationToken ct)
    {
        var snapshot = await store.LoadAsync(ct);
        return ToSettings(snapshot.Document.AspireDeploy.AI);
    }

    public async Task<AIOptions> GetOptionsAsync(CancellationToken ct)
    {
        var snapshot = await store.LoadAsync(ct);
        return ToOptions(snapshot.Document.AspireDeploy.AI);
    }

    public async Task SetOptionsAsync(AIOptions options, CancellationToken ct)
    {
        await store.UpdateAsync(doc =>
        {
            var ai = doc.AspireDeploy.AI ??= new AISection();
            ai.EnableFileTools = options.EnableFileTools;
            ai.MaxLogTailLines = Math.Clamp(options.MaxLogTailLines, 20, 1000);
        }, ct);
        Changed?.Invoke();
    }

    public async Task SaveProviderAsync(AIProviderEdit edit, bool makeActive, CancellationToken ct)
    {
        var needsEndpoint = NeedsEndpoint(edit.Type);
        var endpoint = needsEndpoint ? edit.Endpoint.Trim() : "";
        if (endpoint.Length > 0
            && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new DeployerException(OutcomeCode.InvalidInput, "The endpoint must be a full https URL.");

        await store.UpdateAsync(doc =>
        {
            var ai = doc.AspireDeploy.AI ??= new AISection();
            var key = FindKey(ai, edit.Type) ?? edit.Type.ToString();
            var existing = ai.Providers.GetValueOrDefault(key);

            // D14: the saved key goes only to the endpoint it was saved with.
            if (needsEndpoint && edit.NewApiKey is null && !string.IsNullOrEmpty(existing?.ApiKey)
                && AIKeyEndpointGuard.Normalize(existing.Endpoint) != AIKeyEndpointGuard.Normalize(endpoint))
                throw new DeployerException(OutcomeCode.InvalidInput, "Type the API key again to use it with the new endpoint.");

            var section = existing ?? new AIProviderSection();
            section.AIModel = NullIfEmpty(edit.AIModel);
            section.Endpoint = NullIfEmpty(endpoint);
            section.ApiVersion = edit.Type == AIServiceType.AzureOpenAI ? NullIfEmpty(edit.ApiVersion) : null;
            section.DeploymentPath = edit.Type == AIServiceType.AzureOpenAI ? NullIfEmpty(edit.DeploymentPath) : null;
            section.ApiProtocol = edit.Type == AIServiceType.AzureAIFoundry ? NullIfEmpty(edit.ApiProtocol) : null;
            if (edit.NewApiKey is not null)
                section.ApiKey = NullIfEmpty(edit.NewApiKey);
            ai.Providers[key] = section;
            if (makeActive)
                ai.ActiveServiceType = edit.Type.ToString();
        }, ct);
        Changed?.Invoke();
    }

    public async Task SetActiveAsync(AIServiceType type, CancellationToken ct)
    {
        await store.UpdateAsync(doc => (doc.AspireDeploy.AI ??= new AISection()).ActiveServiceType = type.ToString(), ct);
        Changed?.Invoke();
    }

    public async Task ClearProviderAsync(AIServiceType type, CancellationToken ct)
    {
        await store.UpdateAsync(doc =>
        {
            if (doc.AspireDeploy.AI is not { } ai)
                return;
            foreach (var key in ai.Providers.Keys.Where(k => AIServiceTypes.TryParse(k, out var t) && t == type).ToList())
                ai.Providers.Remove(key);
        }, ct);
        Changed?.Invoke();
    }

    internal static AISettings ToSettings(AISection? ai)
    {
        var settings = new AISettings { ActiveServiceType = AIServiceTypes.ParseOrDefault(ai?.ActiveServiceType) };
        if (ai is null)
            return settings;
        foreach (var (key, section) in ai.Providers)
        {
            // Unknown names are skipped here and kept on save; the first key for a type wins.
            if (section is null || !AIServiceTypes.TryParse(key, out var type) || settings.Providers.ContainsKey(type))
                continue;
            settings.Providers[type] = new AIProviderSettings
            {
                ServiceType = type,
                ApiKey = section.ApiKey ?? "",
                AIModel = section.AIModel ?? "",
                Endpoint = section.Endpoint ?? "",
                ApiVersion = section.ApiVersion ?? "",
                DeploymentPath = section.DeploymentPath ?? "",
                ApiProtocol = section.ApiProtocol ?? ""
            };
        }
        return settings;
    }

    internal static AIOptions ToOptions(AISection? ai) =>
        new(ai?.EnableFileTools ?? true, Math.Clamp(ai?.MaxLogTailLines ?? AIOptions.DefaultMaxLogTailLines, 20, 1000));

    /// <summary>Azure OpenAI and Foundry take a user-supplied endpoint; the others call fixed vendor hosts.</summary>
    public static bool NeedsEndpoint(AIServiceType type) => type is AIServiceType.AzureOpenAI or AIServiceType.AzureAIFoundry;

    private static string? FindKey(AISection ai, AIServiceType type) =>
        ai.Providers.Keys.FirstOrDefault(k => AIServiceTypes.TryParse(k, out var t) && t == type);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
