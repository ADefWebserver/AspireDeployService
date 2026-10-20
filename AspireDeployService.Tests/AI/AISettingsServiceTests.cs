using System.Text.Json;
using System.Text.Json.Nodes;
using AspireDeployService.Core;
using AspireDeployService.Core.AI;
using AspireDeployService.Core.AI.ModelCatalog;
using AspireDeployService.Core.AI.Models;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Settings;
using AspireDeployService.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AspireDeployService.Tests.AI;

public class AISettingsServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _path;
    private readonly AISettingsService _service;

    public AISettingsServiceTests()
    {
        _path = _dir.Combine("appsettings.Local.json");
        File.WriteAllText(_path, """{ "AspireDeploy": { "WorkRoot": "w", "Profiles": {} } }""");
        _service = new AISettingsService(new ProfileStore(new FixedLocator(_path)));
    }

    public void Dispose() => _dir.Dispose();

    private static AIProviderEdit Edit(AIServiceType type, string? key, string model = "gpt-4o", string endpoint = "") =>
        new(type, model, endpoint, "", "", "", key);

    private JsonNode Ai => JsonNode.Parse(File.ReadAllText(_path))!["AspireDeploy"]!["AI"]!;

    [Fact]
    public async Task First_save_creates_the_section_and_null_keeps_the_key()
    {
        var changed = 0;
        _service.Changed += () => changed++;

        await _service.SaveProviderAsync(Edit(AIServiceType.OpenAI, "key-1"), makeActive: true, CancellationToken.None);
        Assert.Equal("OpenAI", (string?)Ai["ActiveServiceType"]);
        Assert.Equal("key-1", (string?)Ai["Providers"]!["OpenAI"]!["ApiKey"]);

        await _service.SaveProviderAsync(Edit(AIServiceType.OpenAI, null, model: "gpt-4.1"), false, CancellationToken.None);
        var settings = await _service.GetAsync(CancellationToken.None);
        Assert.Equal("key-1", settings.Active.ApiKey);
        Assert.Equal("gpt-4.1", settings.Active.AIModel);
        Assert.Equal(2, changed);

        await _service.SaveProviderAsync(Edit(AIServiceType.OpenAI, ""), false, CancellationToken.None);
        Assert.False((await _service.GetAsync(CancellationToken.None)).Active.IsConfigured);
    }

    [Fact]
    public async Task Changed_endpoint_without_the_key_is_refused_and_changes_nothing()
    {
        await _service.SaveProviderAsync(Edit(AIServiceType.AzureAIFoundry, "k", endpoint: "https://a.services.ai.azure.com/"), true, CancellationToken.None);
        var before = File.ReadAllText(_path);

        var ex = await Assert.ThrowsAsync<DeployerException>(() => _service.SaveProviderAsync(
            Edit(AIServiceType.AzureAIFoundry, null, endpoint: "https://evil.example/"), false, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Contains("Type the API key again", ex.Message);
        Assert.Equal(before, File.ReadAllText(_path));

        // Case and a trailing slash are not a change.
        await _service.SaveProviderAsync(Edit(AIServiceType.AzureAIFoundry, null, endpoint: "https://A.services.ai.azure.com"), false, CancellationToken.None);
        // Typing the key again allows the new endpoint.
        await _service.SaveProviderAsync(Edit(AIServiceType.AzureAIFoundry, "k", endpoint: "https://evil.example/"), false, CancellationToken.None);
    }

    [Fact]
    public async Task Http_endpoint_is_refused()
    {
        var ex = await Assert.ThrowsAsync<DeployerException>(() => _service.SaveProviderAsync(
            Edit(AIServiceType.AzureOpenAI, "k", endpoint: "http://x.openai.azure.com"), false, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
    }

    [Fact]
    public async Task Clear_removes_only_that_provider_and_unknown_keys_survive()
    {
        File.WriteAllText(_path, """
            { "AspireDeploy": { "AI": {
                "ActiveServiceType": "claude",
                "Future": 1,
                "Providers": {
                  "Anthropic": { "ApiKey": "a", "AIModel": "claude-x", "Extra1": true },
                  "OpenAI": { "ApiKey": "o" },
                  "Ollama": { "ApiKey": "z" } } } } }
            """);

        var settings = await _service.GetAsync(CancellationToken.None);
        Assert.Equal(AIServiceType.Anthropic, settings.ActiveServiceType);
        Assert.Equal(2, settings.Providers.Count);

        await _service.SaveProviderAsync(Edit(AIServiceType.Anthropic, null, model: "claude-y"), false, CancellationToken.None);
        await _service.ClearProviderAsync(AIServiceType.OpenAI, CancellationToken.None);

        var ai = Ai;
        Assert.Equal(1, (int)ai["Future"]!);
        Assert.True((bool)ai["Providers"]!["Anthropic"]!["Extra1"]!);
        Assert.Equal("claude-y", (string?)ai["Providers"]!["Anthropic"]!["AIModel"]);
        Assert.Null(ai["Providers"]!["OpenAI"]);
        Assert.Equal("z", (string?)ai["Providers"]!["Ollama"]!["ApiKey"]);
    }

    [Fact]
    public async Task A_broken_file_is_never_overwritten()
    {
        File.WriteAllText(_path, "{ not json");
        var ex = await Assert.ThrowsAsync<DeployerException>(() =>
            _service.SaveProviderAsync(Edit(AIServiceType.OpenAI, "k"), true, CancellationToken.None));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Equal("{ not json", File.ReadAllText(_path));
    }

    [Fact]
    public async Task Options_default_and_clamp()
    {
        Assert.Equal(AIOptions.Default, await _service.GetOptionsAsync(CancellationToken.None));
        await _service.SetOptionsAsync(new AIOptions(false, 5), CancellationToken.None);
        Assert.Equal(new AIOptions(false, 20), await _service.GetOptionsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("azure", AIServiceType.AzureOpenAI)]
    [InlineData("foundry", AIServiceType.AzureAIFoundry)]
    [InlineData("gemini", AIServiceType.GoogleAI)]
    [InlineData("nonsense", AIServiceType.OpenAI)]
    public void ParseOrDefault_reads_aliases(string value, AIServiceType expected) =>
        Assert.Equal(expected, AIServiceTypes.ParseOrDefault(value));
}

public class AISettingsTests
{
    [Fact]
    public void Key_alone_is_configured_but_not_ready_for_chat()
    {
        var openAi = new AIProviderSettings { ServiceType = AIServiceType.OpenAI, ApiKey = "k" };
        Assert.True(openAi.IsConfigured);
        Assert.False(openAi.IsReadyForChat);
        openAi.AIModel = "gpt-4o";
        Assert.True(openAi.IsReadyForChat);
    }

    [Theory]
    [InlineData(AIServiceType.AzureOpenAI)]
    [InlineData(AIServiceType.AzureAIFoundry)]
    public void Azure_providers_also_need_an_endpoint(AIServiceType type)
    {
        var p = new AIProviderSettings { ServiceType = type, ApiKey = "k", AIModel = "m" };
        Assert.False(p.IsReadyForChat);
        Assert.Contains("endpoint", p.MissingForChat);
        p.Endpoint = "https://r.services.ai.azure.com";
        Assert.True(p.IsReadyForChat);
        Assert.Null(p.MissingForChat);
    }

    [Fact]
    public void Unknown_provider_names_are_skipped_on_read()
    {
        var section = new AISection();
        section.Providers["Ollama"] = new AIProviderSection { ApiKey = "x" };
        section.Providers["openai"] = new AIProviderSection { ApiKey = "y", AIModel = "m" };
        var settings = AISettingsService.ToSettings(section);
        Assert.Equal(AIServiceType.OpenAI, Assert.Single(settings.Providers).Key);
        Assert.True(settings.IsReadyForChat);
    }
}

public class AIModelListCacheTests
{
    private sealed class CountingCatalog : IAIModelCatalog
    {
        public int Calls;
        public AIServiceType ServiceType => AIServiceType.OpenAI;

        public Task<ModelListResult> ListModelsAsync(AIProviderSettings settings, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ModelListResult.Success(["gpt-4o"]));
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Second_call_is_cached_and_a_different_key_or_endpoint_misses()
    {
        var catalog = new CountingCatalog();
        var time = new ManualTime();
        var cache = new AIModelListCache(new AIProviderRegistry([catalog]), time);
        var settings = new AIProviderSettings { ServiceType = AIServiceType.OpenAI, ApiKey = "k1" };

        await cache.GetAsync(settings, refresh: false, CancellationToken.None);
        await cache.GetAsync(settings, refresh: false, CancellationToken.None);
        Assert.Equal(1, catalog.Calls);

        await cache.GetAsync(new AIProviderSettings { ServiceType = AIServiceType.OpenAI, ApiKey = "k2" }, false, CancellationToken.None);
        await cache.GetAsync(new AIProviderSettings { ServiceType = AIServiceType.OpenAI, ApiKey = "k1", Endpoint = "https://x" }, false, CancellationToken.None);
        Assert.Equal(3, catalog.Calls);

        time.Now += TimeSpan.FromHours(25);
        await cache.GetAsync(settings, refresh: false, CancellationToken.None);
        Assert.Equal(4, catalog.Calls);

        Assert.DoesNotContain("k1", AIModelListCache.CacheKey(settings));
    }
}

public class AIRegistrationTests
{
    [Fact]
    public void Registry_gets_all_five_catalogs()
    {
        using var provider = new ServiceCollection().AddAspireDeployerCore().BuildServiceProvider();
        var registry = provider.GetRequiredService<AIProviderRegistry>();
        foreach (var type in AIServiceTypes.All)
            Assert.NotNull(registry.GetCatalog(type));
        Assert.NotNull(provider.GetRequiredService<AIModelListCache>());
        Assert.NotNull(provider.GetRequiredService<IAISettingsService>());
    }
}
