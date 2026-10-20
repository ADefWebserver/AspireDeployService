using System.Runtime.CompilerServices;
using AspireDeployService.Core.AI;
using AspireDeployService.Core.AI.Models;
using Microsoft.Extensions.AI;

namespace AspireDeployService.Tests.Support;

/// <summary>An <see cref="IChatClient"/> that plays scripted replies, one script per call, and records what it got.</summary>
public sealed class FakeChatClient : IChatClient
{
    public delegate IAsyncEnumerable<ChatResponseUpdate> Reply(CancellationToken ct);

    /// <summary>What each call saw. Options are copied at call time, because the service may change them for a retry.</summary>
    public List<(List<ChatMessage> Messages, float? Temperature, int ToolCount)> Calls { get; } = [];

    public Queue<Reply> Script { get; } = new();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (Calls)
            Calls.Add((messages.ToList(), options?.Temperature, options?.Tools?.Count ?? 0));
        var reply = Script.Count > 0 ? Script.Dequeue() : Text("ok");
        return reply(cancellationToken);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    public static Reply Text(params string[] chunks) => ct => Stream(ct, chunks.Select(c => new ChatResponseUpdate(ChatRole.Assistant, c)));

    public static Reply Throw(Exception ex) => _ => Fail(ex);

    public static Reply Call(string name, Dictionary<string, object?> arguments) => ct => Stream(ct,
        [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments)])]);

    /// <summary>Yields <paramref name="first"/>, then waits until canceled.</summary>
    public static Reply Hang(string first) => ct => HangAsync(first, ct);

    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(
        [EnumeratorCancellation] CancellationToken ct, IEnumerable<ChatResponseUpdate> updates)
    {
        foreach (var update in updates)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return update;
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Fail(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162 // An iterator needs a yield.
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> HangAsync(string first, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, first);
        await Task.Delay(Timeout.Infinite, ct);
    }
}

public sealed class FakeChatClientProvider(FakeChatClient client) : IChatClientProvider
{
    public List<AIProviderSettings> Created { get; } = [];

    public IChatClient Create(AIProviderSettings settings)
    {
        Created.Add(settings.Clone());
        return client;
    }
}
