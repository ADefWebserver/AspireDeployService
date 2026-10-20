using AspireDeployService.Core.AI.Models;
using AspireDeployService.Core.AI.Providers;
using Microsoft.Extensions.AI;

namespace AspireDeployService.Core.AI;

/// <summary>D16: wraps the static <see cref="ChatClientFactory"/> so tests can swap in a fake client.</summary>
public interface IChatClientProvider
{
    /// <exception cref="AIConfigurationException">The settings cannot make a client.</exception>
    IChatClient Create(AIProviderSettings settings);
}

public sealed class ChatClientProvider : IChatClientProvider
{
    public IChatClient Create(AIProviderSettings settings) =>
        ChatClientFactory.Create(settings) ?? throw new AIConfigurationException("Enter an API key for this provider.");
}
