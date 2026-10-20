// Copied from <ref>/src/BlazorDataOrchestrator.Core/Models/AISettings.cs (Blazor-Data-Orchestrator). Re-sync changes from there.
// Changes: EmbeddingModel and the [Obsolete] pass-through members are dropped; IsReadyForChat is new (D5); both classes are sealed.
namespace AspireDeployService.Core.AI.Models;

using ServiceKind = AIServiceType;

/// <summary>
/// Settings for a single AI service provider. Each provider keeps its own
/// API key and provider specific configuration.
/// </summary>
public sealed class AIProviderSettings
{
    public ServiceKind ServiceType { get; set; }
    public string ApiKey { get; set; } = "";
    public string AIModel { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ApiVersion { get; set; } = "";

    /// <summary>
    /// Optional explicit Azure OpenAI deployment path.
    /// Example: "openai/deployments/my-gpt4o" or a full custom route.
    /// When empty, the SDK default convention is used.
    /// </summary>
    public string DeploymentPath { get; set; } = "";

    /// <summary>
    /// Azure AI Foundry wire protocol, stored as a <see cref="FoundryApiProtocol"/> name.
    /// Empty means <see cref="FoundryApiProtocol.Auto"/>.
    /// </summary>
    public string ApiProtocol { get; set; } = "";

    public FoundryApiProtocol ParsedApiProtocol =>
        Enum.TryParse<FoundryApiProtocol>(ApiProtocol, ignoreCase: true, out var protocol)
            ? protocol
            : FoundryApiProtocol.Auto;

    /// <summary>A key is set. Gates model listing and the connection test.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>A key and a model are set, plus an endpoint where the provider needs one (D5). Gates AI Help.</summary>
    public bool IsReadyForChat =>
        IsConfigured
        && !string.IsNullOrWhiteSpace(AIModel)
        && (ServiceType is not (ServiceKind.AzureOpenAI or ServiceKind.AzureAIFoundry) || !string.IsNullOrWhiteSpace(Endpoint));

    /// <summary>The first field missing for <see cref="IsReadyForChat"/>, worded for the Settings card; null when ready.</summary>
    public string? MissingForChat =>
        !IsConfigured ? "Enter an API key to turn on AI Help."
        : ServiceType is ServiceKind.AzureOpenAI or ServiceKind.AzureAIFoundry && string.IsNullOrWhiteSpace(Endpoint) ? "Enter the endpoint to turn on AI Help."
        : string.IsNullOrWhiteSpace(AIModel) ? "Choose a model to turn on AI Help."
        : null;

    public AIProviderSettings Clone() => new()
    {
        ServiceType = ServiceType,
        ApiKey = ApiKey,
        AIModel = AIModel,
        Endpoint = Endpoint,
        ApiVersion = ApiVersion,
        DeploymentPath = DeploymentPath,
        ApiProtocol = ApiProtocol
    };
}

/// <summary>
/// Container holding the settings for every AI provider plus the active selection.
/// </summary>
public sealed class AISettings
{
    public ServiceKind ActiveServiceType { get; set; } = ServiceKind.OpenAI;

    public Dictionary<ServiceKind, AIProviderSettings> Providers { get; set; } = new();

    public AIProviderSettings Active => GetOrCreate(ActiveServiceType);

    public AIProviderSettings GetOrCreate(ServiceKind type)
    {
        if (!Providers.TryGetValue(type, out var provider))
        {
            provider = new AIProviderSettings { ServiceType = type };
            Providers[type] = provider;
        }

        return provider;
    }

    public bool IsConfigured => Active.IsConfigured;

    public bool IsReadyForChat => Active.IsReadyForChat;

    public AISettings Clone()
    {
        var clone = new AISettings { ActiveServiceType = ActiveServiceType };

        foreach (var pair in Providers)
        {
            clone.Providers[pair.Key] = pair.Value.Clone();
        }

        return clone;
    }
}
