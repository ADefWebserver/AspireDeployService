// Copied from <ref>/tests/BlazorOrchestrator.Web.Tests/SecurityReviewFixTests.cs (Blazor-Data-Orchestrator), the SEC-003
// AIKeyEndpointGuard cases only. Re-sync changes from there.
using AspireDeployService.Core.AI.Models;
using AspireDeployService.Core.AI.Providers;

namespace AspireDeployService.Tests.Copied;

public class AIKeyEndpointGuardTests
{
    [Fact(DisplayName = "SEC-003: a stored key is only sent to the endpoint it was saved with")]
    [Trait("Category", "Security")]
    public void KeyGuard_BlocksStoredKeyOnChangedEndpoint()
    {
        var guard = new AIKeyEndpointGuard();
        var provider = new AIProviderSettings { ApiKey = "stored-key", Endpoint = "https://contoso.openai.azure.com/" };
        guard.TrustSaved(provider);

        Assert.True(guard.CanSendKey(AIServiceType.AzureOpenAI, provider));

        provider.Endpoint = "https://CONTOSO.openai.azure.com";
        Assert.True(guard.CanSendKey(AIServiceType.AzureOpenAI, provider));

        provider.Endpoint = "https://attacker.example/";
        Assert.False(guard.CanSendKey(AIServiceType.AzureOpenAI, provider));
        Assert.False(guard.CanSendKey(AIServiceType.AzureAIFoundry, provider));

        // Re-entering the key is an explicit choice to use it with the new endpoint.
        provider.ApiKey = "typed-key";
        guard.TrustUserEnteredKey("typed-key");
        Assert.True(guard.CanSendKey(AIServiceType.AzureOpenAI, provider));
    }

    [Fact(DisplayName = "SEC-003: fixed-host providers are not restricted by the key guard")]
    [Trait("Category", "Security")]
    public void KeyGuard_IgnoresFixedHostProviders()
    {
        var guard = new AIKeyEndpointGuard();
        var provider = new AIProviderSettings { ApiKey = "sk-stored", Endpoint = "https://anything.example" };

        Assert.True(guard.CanSendKey(AIServiceType.OpenAI, provider));
        Assert.True(guard.CanSendKey(AIServiceType.Anthropic, provider));
    }
}
