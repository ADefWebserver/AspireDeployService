using AspireDeployService.Core.AI;
using AspireDeployService.Core.AI.Models;

namespace AspireDeployService.Tests.AI;

public class AISettingsEditSessionTests
{
    private static AISettings Saved()
    {
        var s = new AISettings { ActiveServiceType = AIServiceType.AzureAIFoundry };
        s.Providers[AIServiceType.AzureAIFoundry] = new AIProviderSettings
        {
            ServiceType = AIServiceType.AzureAIFoundry, ApiKey = "foundry-key", AIModel = "gpt-4o",
            Endpoint = "https://r.services.ai.azure.com/"
        };
        s.Providers[AIServiceType.OpenAI] = new AIProviderSettings { ServiceType = AIServiceType.OpenAI, ApiKey = "openai-key", AIModel = "gpt-4.1" };
        return s;
    }

    [Fact]
    public void Switching_provider_loads_its_values()
    {
        var session = new AISettingsEditSession(Saved());
        Assert.Equal(AIServiceType.AzureAIFoundry, session.Type);
        Assert.True(session.IsActive);
        session.Select(AIServiceType.OpenAI);
        Assert.Equal("gpt-4.1", session.AIModel);
        Assert.Equal("", session.Endpoint);
        Assert.False(session.IsActive);
        Assert.True(session.KeyIsSet);
        session.Select(AIServiceType.Anthropic);
        Assert.False(session.KeyIsSet);
        Assert.Equal("Enter an API key to turn on AI Help.", session.MissingForChat);
    }

    [Fact]
    public void Save_produces_the_edit_and_clear_key_gives_empty()
    {
        var session = new AISettingsEditSession(Saved(), AIServiceType.OpenAI);
        session.AIModel = " gpt-5 ";
        Assert.Equal(new AIProviderEdit(AIServiceType.OpenAI, "gpt-5", "", "", "", "", null), session.ToEdit());
        session.ClearKey();
        Assert.Equal("", session.ToEdit().NewApiKey);
        Assert.False(session.KeyIsSet);
        session.SetNewKey("  typed  ");
        Assert.Equal("typed", session.ToEdit().NewApiKey);
    }

    [Fact]
    public void Validation_messages()
    {
        var session = new AISettingsEditSession(Saved(), AIServiceType.AzureOpenAI);
        session.Endpoint = "http://x.openai.azure.com";
        Assert.Contains("The endpoint must be a full https URL.", session.Validate());
        session.Endpoint = "https://x.openai.azure.com";
        Assert.Empty(session.Validate());

        var fresh = new AISettingsEditSession(new AISettings(), AIServiceType.OpenAI);
        fresh.SetNewKey("k");
        Assert.Equal("Choose a model to turn on AI Help.", fresh.MissingForChat);
    }

    [Fact]
    public void Changed_endpoint_blocks_the_saved_key_until_it_is_typed_again()
    {
        var session = new AISettingsEditSession(Saved());
        Assert.True(session.CanSendKey);

        session.Endpoint = "https://R.services.ai.azure.com";
        Assert.True(session.CanSendKey); // case and trailing slash are not a change

        session.Endpoint = "https://attacker.example/";
        Assert.False(session.CanSendKey);
        Assert.Contains(AISettingsEditSession.RetypeKeyMessage, session.Validate());

        session.SetNewKey("foundry-key");
        Assert.True(session.CanSendKey);
        Assert.Empty(session.Validate());
    }

    [Fact]
    public void Foundry_preview_shows_the_resolved_url()
    {
        var session = new AISettingsEditSession(Saved());
        var (url, _, error) = session.FoundryPreview();
        Assert.Null(error);
        Assert.StartsWith("https://r.services.ai.azure.com/openai/v1/chat/completions", url);
    }
}
