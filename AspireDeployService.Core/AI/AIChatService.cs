using System.Runtime.CompilerServices;
using System.Text;
using AspireDeployService.Core.AI.Models;
using AspireDeployService.Core.AI.Providers;
using AspireDeployService.Core.Settings;
using Microsoft.Extensions.AI;

namespace AspireDeployService.Core.AI;

public enum AIChatRole { User, Assistant }

/// <param name="Stopped">The user pressed Stop; Text is the partial reply.</param>
/// <param name="Failed">The reply failed; Text is empty and the panel shows the error with Retry.</param>
/// <param name="Activity">One line per tool call made while this reply was written.</param>
public sealed record AIChatTurn(AIChatRole Role, string Text, bool Stopped = false, bool Failed = false,
    IReadOnlyList<string>? Activity = null);

/// <summary>One streamed item: a text delta, or one line describing a tool call ("Read src/App/App.csproj from line 1").</summary>
public sealed record AIChatUpdate(string? Text, string? ToolActivity);

public sealed class AIChatException(string message, Exception inner) : Exception(message, inner);

/// <summary>One chat about one <see cref="AIHelpContext"/>. Lives in memory only (plan Q4).</summary>
public sealed class AIChatSession
{
    private readonly List<AIChatTurn> _turns = [];
    private int _busy;

    internal AIChatSession(AIHelpContext context, string contextText)
    {
        Context = context;
        ContextText = contextText;
    }

    public AIHelpContext Context { get; }

    /// <summary>Exactly what is sent as context, already scrubbed; shown in "Context sent".</summary>
    public string ContextText { get; internal set; }

    public IReadOnlyList<AIChatTurn> Turns
    {
        get { lock (_turns) return _turns.ToArray(); }
    }

    /// <summary>One send at a time; a second send while busy throws <see cref="InvalidOperationException"/>.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    public bool ToolsEnabled { get; internal set; }
    public string? ToolsUnavailableReason { get; internal set; }
    public AIServiceType Provider { get; internal set; }
    public string Model { get; internal set; } = "";

    internal void Add(AIChatTurn turn)
    {
        lock (_turns) _turns.Add(turn);
    }

    internal void RemoveLastIf(Func<AIChatTurn, bool> predicate)
    {
        lock (_turns)
        {
            if (_turns.Count > 0 && predicate(_turns[^1]))
                _turns.RemoveAt(_turns.Count - 1);
        }
    }

    internal void EnterBusy()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("A reply is already being written for this chat.");
    }

    internal void ExitBusy() => Volatile.Write(ref _busy, 0);
}

public interface IAIChatService
{
    /// <summary>Throws <see cref="AIConfigurationException"/> when the active provider is not ready for chat (D5).</summary>
    Task<AIChatSession> StartAsync(AIHelpContext context, CancellationToken ct);

    /// <summary>
    /// Scrubs <paramref name="userText"/>, appends a User turn, streams updates, then appends the Assistant turn
    /// (Stopped on cancellation, Failed on error). On error throws <see cref="AIChatException"/> with a scrubbed message.
    /// </summary>
    IAsyncEnumerable<AIChatUpdate> SendAsync(AIChatSession session, string userText, CancellationToken ct);

    /// <summary>Removes the last Failed or Stopped Assistant turn and re-sends the last User turn without appending it again.</summary>
    IAsyncEnumerable<AIChatUpdate> RetryAsync(AIChatSession session, CancellationToken ct);
}

/// <summary>AI Help chat (plan §6.4): context, history, read-only tools, true streaming (D10).</summary>
public sealed class AIChatService(IAISettingsService settings, AIPromptScrubber scrubber, IChatClientProvider clients,
    ProfileStore store) : IAIChatService
{
    public const int MaxHistoryTurns = 20;
    public const int MaxToolIterations = 12;
    public const int MaxOutputTokens = 4096;

    internal const string SystemPrompt = """
        You help a developer deploy .NET Aspire apps to Azure with Aspire Deploy, a Windows desktop app that runs the
        Aspire CLI (discover, setup checks, deploy) for them.
        The first user message is context captured from the app: what failed or what the user is looking at, with
        facts and the end of the run log. Later messages are the developer's questions.
        Be concrete and short. When a file must change, name the file and quote the line to change.
        Write plain text. Use fenced code blocks for code and `backticks` for file names, commands and settings; the
        app shows no other Markdown, so do not use bold, italics, headings or tables.
        Say clearly when the cause is in the user's own project (package versions, AppHost code, Azure permissions)
        rather than in Aspire Deploy.
        Never ask for passwords, keys or tokens. Values shown as [REDACTED] were hidden on purpose; do not guess them.
        If you have tools, you can only read files under the run's source folder and the run log. You cannot change
        files, run commands or call Azure; tell the developer what to do instead.
        """;

    public async Task<AIChatSession> StartAsync(AIHelpContext context, CancellationToken ct)
    {
        var active = (await settings.GetAsync(ct)).Active;
        if (!active.IsReadyForChat)
            throw new AIConfigurationException(active.MissingForChat ?? "AI Help is not set up.");
        var options = await settings.GetOptionsAsync(ct);
        var redactor = await scrubber.CreateAsync(ct);
        var reason = ToolsUnavailableReason(active, EffectiveProtocol(active), options, context);
        var withTools = context with { Facts = [.. context.Facts, new("File tools", ToolsFact(reason, context))] };
        var session = new AIChatSession(context, redactor.Scrub(AIContextFormatter.Format(withTools, options.MaxLogTailLines)));
        Describe(session, active, options);
        return session;
    }

    /// <summary>Tells the model what it can read, so it never claims to have looked at a file it cannot open.</summary>
    internal static string ToolsFact(string? unavailableReason, AIHelpContext context) =>
        unavailableReason is not null ? "none. " + unavailableReason
        : context.SourceRoot is null ? "can read this run's log"
        : "can read files under the source folder" + (context.LogFile is null ? "" : " and this run's log");

    public async IAsyncEnumerable<AIChatUpdate> SendAsync(AIChatSession session, string userText,
        [EnumeratorCancellation] CancellationToken ct)
    {
        session.EnterBusy();
        try
        {
            var redactor = await scrubber.CreateAsync(ct);
            session.Add(new AIChatTurn(AIChatRole.User, redactor.Scrub(userText.Trim())));
            await foreach (var update in StreamAsync(session, redactor, ct))
                yield return update;
        }
        finally
        {
            session.ExitBusy();
        }
    }

    public async IAsyncEnumerable<AIChatUpdate> RetryAsync(AIChatSession session, [EnumeratorCancellation] CancellationToken ct)
    {
        session.EnterBusy();
        try
        {
            session.RemoveLastIf(t => t.Role == AIChatRole.Assistant && (t.Failed || t.Stopped));
            if (session.Turns is not [.., { Role: AIChatRole.User }])
                yield break;
            var redactor = await scrubber.CreateAsync(ct);
            await foreach (var update in StreamAsync(session, redactor, ct))
                yield return update;
        }
        finally
        {
            session.ExitBusy();
        }
    }

    private async IAsyncEnumerable<AIChatUpdate> StreamAsync(AIChatSession session, IPromptRedactor redactor,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var reply = new StringBuilder();
        var activity = new List<string>();
        IChatClient? client = null;
        IAsyncEnumerator<ChatResponseUpdate>? stream = null;
        FoundryApiProtocol protocol = FoundryApiProtocol.Auto;
        AIProviderSettings? active = null;
        try
        {
            ChatOptions options;
            List<ChatMessage> messages;
            try
            {
                // Settings may change between turns, so every send starts from a fresh read.
                active = (await settings.GetAsync(ct)).Active;
                if (!active.IsReadyForChat)
                    throw new AIConfigurationException(active.MissingForChat ?? "AI Help is not set up.");
                var aiOptions = await settings.GetOptionsAsync(ct);
                protocol = Describe(session, active, aiOptions);
                session.ContextText = redactor.Scrub(session.ContextText);
                messages = BuildMessages(session, redactor);
                options = new ChatOptions { MaxOutputTokens = MaxOutputTokens };
                if (ModelCapabilities.SupportsTemperature(active.AIModel, protocol))
                    options.Temperature = 0.2f;

                client = clients.Create(active);
                if (session.ToolsEnabled)
                {
                    var tools = new AIFileTools(session.Context.SourceRoot, session.Context.LogFile, DeniedPaths(), redactor);
                    options.Tools = tools.AsTools();
                    client = new ChatClientBuilder(client)
                        .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = MaxToolIterations)
                        .Build();
                }
                stream = client.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Fail(session, ex, active, redactor);
            }

            var started = false;
            var relaxed = false;
            while (true)
            {
                bool moved;
                try
                {
                    moved = await stream.MoveNextAsync();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    session.Add(new AIChatTurn(AIChatRole.Assistant, reply.ToString(), Stopped: true, Activity: activity));
                    yield break;
                }
                catch (Exception ex) when (!started && !relaxed && TryRelax(ex, options))
                {
                    // One retry without the option the deployment rejected (copied ref behaviour, plan §7.3).
                    relaxed = true;
                    await stream.DisposeAsync();
                    stream = client.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
                    continue;
                }
                catch (Exception ex)
                {
                    throw Fail(session, ex, active, redactor);
                }

                if (!moved)
                    break;
                started = true;

                foreach (var call in stream.Current.Contents.OfType<FunctionCallContent>())
                {
                    var line = redactor.Scrub(AIFileTools.Describe(call));
                    activity.Add(line);
                    yield return new AIChatUpdate(null, line);
                }
                if (stream.Current.Text is { Length: > 0 } text)
                {
                    reply.Append(text);
                    yield return new AIChatUpdate(text, null);
                }
            }
            session.Add(new AIChatTurn(AIChatRole.Assistant, reply.ToString(), Activity: activity));
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync();
            client?.Dispose();
        }
    }

    private static AIChatException Fail(AIChatSession session, Exception ex, AIProviderSettings? active, IPromptRedactor redactor)
    {
        session.Add(new AIChatTurn(AIChatRole.Assistant, "", Failed: true));
        var message = ex is AIConfigurationException ? ex.Message : AIErrorTranslator.Translate(ex, TryResolve(active), active);
        return new AIChatException(redactor.Scrub(message), ex);
    }

    private static List<ChatMessage> BuildMessages(AIChatSession session, IPromptRedactor redactor)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, session.ContextText)
        };
        foreach (var turn in session.Turns.Where(t => !t.Failed).TakeLast(MaxHistoryTurns))
        {
            messages.Add(new ChatMessage(turn.Role == AIChatRole.User ? ChatRole.User : ChatRole.Assistant,
                turn.Role == AIChatRole.User ? redactor.Scrub(turn.Text) : turn.Text));
        }
        return messages;
    }

    /// <summary>Sets the session's provider, model and tool state; returns the effective protocol (plan §6.4).</summary>
    private static FoundryApiProtocol Describe(AIChatSession session, AIProviderSettings active, AIOptions options)
    {
        var protocol = EffectiveProtocol(active);
        session.Provider = active.ServiceType;
        session.Model = active.AIModel;
        session.ToolsUnavailableReason = ToolsUnavailableReason(active, protocol, options, session.Context);
        session.ToolsEnabled = session.ToolsUnavailableReason is null;
        return protocol;
    }

    internal static string? ToolsUnavailableReason(AIProviderSettings active, FoundryApiProtocol protocol, AIOptions options,
        AIHelpContext context)
    {
        if (!options.EnableFileTools)
            return "File tools are turned off in Settings.";
        if (active.ServiceType is AIServiceType.Anthropic or AIServiceType.GoogleAI)
            return $"File tools aren't available with {AIServiceTypes.ToDisplayName(active.ServiceType)}.";
        if (protocol == FoundryApiProtocol.AnthropicMessages)
            return "File tools aren't available with the Anthropic Messages protocol.";
        if (!ModelCapabilities.SupportsTools(active.AIModel, protocol))
            return $"File tools aren't available with the model {active.AIModel}.";
        if (string.IsNullOrEmpty(context.SourceRoot) && string.IsNullOrEmpty(context.LogFile))
            return "There is no source folder or run log to read for this topic.";
        return null;
    }

    /// <summary>Foundry (and Azure OpenAI on Foundry routing) resolves Auto from the URL or model, maybe to Anthropic Messages.</summary>
    internal static FoundryApiProtocol EffectiveProtocol(AIProviderSettings settings) =>
        TryResolve(settings)?.Protocol ?? settings.ParsedApiProtocol;

    private static FoundryEndpointResolution? TryResolve(AIProviderSettings? settings)
    {
        if (settings is null)
            return null;
        var usesFoundry = settings.ServiceType == AIServiceType.AzureAIFoundry
            || settings.ServiceType == AIServiceType.AzureOpenAI && FoundryEndpointResolver.ShouldUseV1Routing(settings.Endpoint);
        return usesFoundry
            && FoundryEndpointResolver.TryResolve(settings.Endpoint, settings.AIModel, settings.ParsedApiProtocol, out var resolution, out _)
            ? resolution
            : null;
    }

    private static bool TryRelax(Exception ex, ChatOptions options)
    {
        if (AIErrorTranslator.ExtractStatus(ex) != 400)
            return false;
        var message = ex.Message ?? "";
        if (options.Temperature is not null && message.Contains("temperature", StringComparison.OrdinalIgnoreCase))
        {
            options.Temperature = null;
            return true;
        }
        if (options.Tools is { Count: > 0 } && message.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            options.Tools = null;
            return true;
        }
        return false;
    }

    private string[] DeniedPaths()
    {
        var path = store.Location.Path;
        return [path, path + ".lock", path + ".tmp"];
    }
}
