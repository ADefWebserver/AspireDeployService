using System.Net;
using AspireDeployService.Core.AI;
using AspireDeployService.Core.AI.Models;
using AspireDeployService.Core.AI.Providers;
using AspireDeployService.Core.Settings;
using AspireDeployService.Tests.Support;
using Microsoft.Extensions.AI;

namespace AspireDeployService.Tests.AI;

public class AIChatServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _path;
    private readonly FakeChatClient _client = new();
    private readonly AIChatService _service;
    private readonly AISettingsService _settings;

    public AIChatServiceTests()
    {
        _path = _dir.Combine("appsettings.Local.json");
        Write("OpenAI", """{ "OpenAI": { "ApiKey": "openai-secret-key", "AIModel": "gpt-4o" } }""");
        _dir.Write("src/App/App.csproj", "<Project />");
        var store = new ProfileStore(new FixedLocator(_path));
        _settings = new AISettingsService(store);
        _service = new AIChatService(_settings, new AIPromptScrubber(store), new FakeChatClientProvider(_client), store);
    }

    public void Dispose() => _dir.Dispose();

    private void Write(string active, string providers) => File.WriteAllText(_path,
        $$"""{ "AspireDeploy": { "GitHub": { "Token": "gh-global-token" }, "AI": { "ActiveServiceType": "{{active}}", "Providers": {{providers}} } } }""");

    private AIHelpContext Context(string summary = "Discovery failed: exit 6") => new(
        "run:1", "Discovery failed", AIContextKind.RunResult, summary, [new("Page", "Discover")], [],
        _dir.Combine("src"), null);

    private static async Task<List<AIChatUpdate>> Collect(IAsyncEnumerable<AIChatUpdate> updates)
    {
        var list = new List<AIChatUpdate>();
        await foreach (var u in updates)
            list.Add(u);
        return list;
    }

    [Fact]
    public async Task Updates_stream_as_they_arrive_and_context_is_scrubbed()
    {
        var session = await _service.StartAsync(Context("token gh-global-token leaked"), CancellationToken.None);
        Assert.DoesNotContain("gh-global-token", session.ContextText);
        Assert.True(session.ToolsEnabled);

        _client.Script.Enqueue(FakeChatClient.Text("Hel", "lo"));
        var updates = await Collect(_service.SendAsync(session, "my key is openai-secret-key", CancellationToken.None));

        Assert.Equal(["Hel", "lo"], updates.Select(u => u.Text));
        var call = Assert.Single(_client.Calls);
        Assert.Equal(ChatRole.System, call.Messages[0].Role);
        Assert.Equal(session.ContextText, call.Messages[1].Text);
        Assert.DoesNotContain("openai-secret-key", call.Messages[2].Text);
        Assert.Equal(0.2f, call.Temperature);
        Assert.Equal(3, call.ToolCount); // no log file, so no read_run_log
        Assert.Equal("Hello", session.Turns[^1].Text);
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task Tool_calls_become_activity_and_run_the_real_tool()
    {
        var session = await _service.StartAsync(Context(), CancellationToken.None);
        _client.Script.Enqueue(FakeChatClient.Call("read_file", new() { ["relativePath"] = "App/App.csproj" }));
        _client.Script.Enqueue(FakeChatClient.Text("Done"));

        var updates = await Collect(_service.SendAsync(session, "look", CancellationToken.None));

        Assert.Contains(updates, u => u.ToolActivity == "Read App/App.csproj from line 1");
        Assert.Equal(2, _client.Calls.Count);
        Assert.Contains(_client.Calls[1].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => r.Result?.ToString()?.Contains("<Project />") == true);
        Assert.Equal("Done", session.Turns[^1].Text);
        Assert.Single(session.Turns[^1].Activity!);
    }

    [Fact]
    public async Task History_is_capped_and_skips_failed_turns()
    {
        var session = await _service.StartAsync(Context(), CancellationToken.None);
        _client.Script.Enqueue(FakeChatClient.Throw(new HttpRequestException("boom", null, HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<AIChatException>(() => Collect(_service.SendAsync(session, "q0", CancellationToken.None)));
        for (var i = 1; i <= 12; i++)
            await Collect(_service.SendAsync(session, $"q{i}", CancellationToken.None));

        var last = _client.Calls[^1].Messages;
        Assert.Equal(2 + AIChatService.MaxHistoryTurns, last.Count);
        Assert.DoesNotContain(last, m => m.Role == ChatRole.Assistant && m.Text == "");
    }

    [Theory]
    [InlineData("Anthropic", """{ "Anthropic": { "ApiKey": "k", "AIModel": "claude-sonnet-4" } }""", "Anthropic")]
    [InlineData("GoogleAI", """{ "GoogleAI": { "ApiKey": "k", "AIModel": "gemini-2.5-pro" } }""", "Google AI")]
    [InlineData("AzureAIFoundry", """{ "AzureAIFoundry": { "ApiKey": "k", "AIModel": "gpt-4o", "Endpoint": "https://r.services.ai.azure.com/anthropic/v1/messages" } }""", "Anthropic Messages")]
    public async Task Tools_are_off_with_a_reason(string active, string providers, string reasonPart)
    {
        Write(active, providers);
        var session = await _service.StartAsync(Context(), CancellationToken.None);
        Assert.False(session.ToolsEnabled);
        Assert.Contains(reasonPart, session.ToolsUnavailableReason);

        await Collect(_service.SendAsync(session, "hi", CancellationToken.None));
        Assert.Equal(0, _client.Calls[^1].ToolCount);
    }

    [Fact]
    public async Task Rejected_temperature_is_retried_once_without_it()
    {
        var session = await _service.StartAsync(Context(), CancellationToken.None);
        _client.Script.Enqueue(FakeChatClient.Throw(new HttpRequestException(
            "Unsupported value: 'temperature' does not support 0.2", null, HttpStatusCode.BadRequest)));
        _client.Script.Enqueue(FakeChatClient.Text("fine"));

        await Collect(_service.SendAsync(session, "hi", CancellationToken.None));

        Assert.Equal(2, _client.Calls.Count);
        Assert.Equal(0.2f, _client.Calls[0].Temperature);
        Assert.Null(_client.Calls[1].Temperature);
        Assert.Equal("fine", session.Turns[^1].Text);
    }

    [Fact]
    public async Task Cancel_mid_stream_keeps_a_stopped_turn_and_retry_does_not_duplicate_the_question()
    {
        var session = await _service.StartAsync(Context(), CancellationToken.None);
        _client.Script.Enqueue(FakeChatClient.Hang("partial"));
        using var cts = new CancellationTokenSource();
        await foreach (var update in _service.SendAsync(session, "question", cts.Token))
        {
            Assert.Equal("partial", update.Text);
            cts.Cancel();
        }

        Assert.Equal(new AIChatTurn(AIChatRole.Assistant, "partial", Stopped: true), session.Turns[^1] with { Activity = null });

        _client.Script.Enqueue(FakeChatClient.Text("whole"));
        await Collect(_service.RetryAsync(session, CancellationToken.None));

        Assert.Equal(2, session.Turns.Count);
        Assert.Equal("question", session.Turns[0].Text);
        Assert.Equal("whole", session.Turns[1].Text);
        Assert.Single(_client.Calls[^1].Messages, m => m.Text == "question");
    }

    [Fact]
    public async Task A_second_concurrent_send_throws()
    {
        var session = await _service.StartAsync(Context(), CancellationToken.None);
        _client.Script.Enqueue(FakeChatClient.Hang("x"));
        using var cts = new CancellationTokenSource();
        var first = _service.SendAsync(session, "one", cts.Token).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(_service.SendAsync(session, "two", CancellationToken.None)));

        cts.Cancel();
        while (await first.MoveNextAsync()) { }
        await first.DisposeAsync();
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task Start_refuses_a_provider_without_a_model()
    {
        Write("OpenAI", """{ "OpenAI": { "ApiKey": "k" } }""");
        var ex = await Assert.ThrowsAsync<AIConfigurationException>(() => _service.StartAsync(Context(), CancellationToken.None));
        Assert.Contains("model", ex.Message);
    }

    [Fact]
    public async Task Errors_are_translated_and_scrubbed()
    {
        var session = await _service.StartAsync(Context(), CancellationToken.None);
        _client.Script.Enqueue(FakeChatClient.Throw(new InvalidOperationException("failed with openai-secret-key")));
        var ex = await Assert.ThrowsAsync<AIChatException>(() => Collect(_service.SendAsync(session, "hi", CancellationToken.None)));
        Assert.DoesNotContain("openai-secret-key", ex.Message);
        Assert.True(session.Turns[^1].Failed);
    }

    [Fact]
    public void Effective_protocol_resolves_foundry_auto_from_the_model()
    {
        var foundry = new AIProviderSettings
        {
            ServiceType = AIServiceType.AzureAIFoundry, ApiKey = "k", AIModel = "claude-sonnet-4-6",
            Endpoint = "https://r.services.ai.azure.com"
        };
        Assert.Equal(FoundryApiProtocol.AnthropicMessages, AIChatService.EffectiveProtocol(foundry));
    }
}
