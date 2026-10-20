using AspireDeployService.Core.AI.Models;
using AspireDeployService.Core.AI.Providers;

namespace AspireDeployService.Core.AI;

/// <summary>
/// Form state for the Settings page's AI assistant card (AI Help plan §6.2), testable without Razor like
/// <c>ProfileEditSession</c>. Owns one <see cref="AIKeyEndpointGuard"/> (D14): the saved key goes only to the endpoint
/// it was saved with until the user types the key again, for Test connection, model listing and Save alike.
/// </summary>
public sealed class AISettingsEditSession
{
    private readonly AIKeyEndpointGuard _guard = new();
    private AISettings _saved = new();

    public AISettingsEditSession(AISettings saved, AIServiceType? select = null)
    {
        Load(saved);
        Select(select ?? saved.ActiveServiceType);
    }

    public AIServiceType Type { get; private set; }
    public AIServiceType ActiveType => _saved.ActiveServiceType;
    public bool IsActive => Type == ActiveType;

    public string AIModel { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string DeploymentPath { get; set; } = "";
    public string ApiProtocol { get; set; } = "";

    /// <summary>null keeps the saved key; "" clears it; any other value replaces it.</summary>
    public string? NewApiKey { get; private set; }

    public bool HasSavedKey => _saved.Providers.TryGetValue(Type, out var p) && p.IsConfigured;
    public bool HasSavedEntry => _saved.Providers.ContainsKey(Type);
    public bool KeyIsSet => NewApiKey is null ? HasSavedKey : NewApiKey.Length > 0;
    public bool NeedsEndpoint => AISettingsService.NeedsEndpoint(Type);

    /// <summary>Replaces the whole saved state (after a save or a fresh read) and keeps the provider being edited.</summary>
    public void Load(AISettings saved)
    {
        _saved = saved.Clone();
        foreach (var provider in _saved.Providers.Values)
            _guard.TrustSaved(provider);
    }

    /// <summary>Switches the card to <paramref name="type"/> and loads its saved values; a typed key is dropped.</summary>
    public void Select(AIServiceType type)
    {
        Type = type;
        var saved = _saved.Providers.GetValueOrDefault(type) ?? new AIProviderSettings { ServiceType = type };
        AIModel = saved.AIModel;
        Endpoint = saved.Endpoint;
        ApiVersion = saved.ApiVersion;
        DeploymentPath = saved.DeploymentPath;
        ApiProtocol = saved.ApiProtocol;
        NewApiKey = null;
    }

    public void SetNewKey(string? key)
    {
        var trimmed = key?.Trim() ?? "";
        NewApiKey = trimmed.Length == 0 ? null : trimmed;
        if (NewApiKey is not null)
            _guard.TrustUserEnteredKey(NewApiKey);
    }

    public void ClearKey() => NewApiKey = "";

    public void KeepKey() => NewApiKey = null;

    /// <summary>The values in the form, with a typed key or else the saved one. Used for Test and model listing.</summary>
    public AIProviderSettings Effective() => new()
    {
        ServiceType = Type,
        ApiKey = NewApiKey ?? _saved.Providers.GetValueOrDefault(Type)?.ApiKey ?? "",
        AIModel = AIModel.Trim(),
        Endpoint = NeedsEndpoint ? Endpoint.Trim() : "",
        ApiVersion = Type == AIServiceType.AzureOpenAI ? ApiVersion.Trim() : "",
        DeploymentPath = Type == AIServiceType.AzureOpenAI ? DeploymentPath.Trim() : "",
        ApiProtocol = Type == AIServiceType.AzureAIFoundry ? ApiProtocol : ""
    };

    /// <summary>D14: false when the saved key would go to an endpoint it was not saved with.</summary>
    public bool CanSendKey => _guard.CanSendKey(Type, Effective());

    public const string RetypeKeyMessage = "Type the API key again to use it with this endpoint.";

    /// <summary>The first field missing before AI Help can use this provider, e.g. "Choose a model to turn on AI Help."</summary>
    public string? MissingForChat => Effective().MissingForChat;

    /// <summary>Foundry only: the resolved request URL and notices, or the resolver's error.</summary>
    public (string? Url, IReadOnlyList<string> Notices, string? Error) FoundryPreview()
    {
        if (Type != AIServiceType.AzureAIFoundry || string.IsNullOrWhiteSpace(Endpoint))
            return (null, [], null);
        var settings = Effective();
        return FoundryEndpointResolver.TryResolve(settings.Endpoint, settings.AIModel, settings.ParsedApiProtocol, out var r, out var error)
            ? (r!.OperationUrl + " (" + FoundryEndpointResolver.Describe(r.Protocol) + ")", r.Notices, null)
            : (null, [], error);
    }

    /// <summary>Messages that block Save; empty when the form can be saved.</summary>
    public IReadOnlyList<string> Validate()
    {
        var messages = new List<string>();
        var endpoint = Endpoint.Trim();
        if (NeedsEndpoint && endpoint.Length > 0
            && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            messages.Add("The endpoint must be a full https URL.");
        if (!CanSendKey)
            messages.Add(RetypeKeyMessage);
        return messages;
    }

    public AIProviderEdit ToEdit()
    {
        var e = Effective();
        return new AIProviderEdit(Type, e.AIModel, e.Endpoint, e.ApiVersion, e.DeploymentPath, e.ApiProtocol, NewApiKey);
    }
}
