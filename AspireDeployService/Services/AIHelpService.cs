using AspireDeployService.Core.AI;

namespace AspireDeployService.Services;

/// <summary>
/// AI Help plan §5.2: whether AI Help is ready, which context the chat panel shows, and the chat sessions (in memory,
/// at most <see cref="MaxSessions"/>, least recently used dropped). <see cref="Changed"/> may fire on any thread, so
/// every handler uses <c>InvokeAsync</c>.
/// </summary>
public sealed class AIHelpService : IDisposable
{
    public const int MaxSessions = 20;

    private readonly IAISettingsService _settings;
    private readonly object _gate = new();
    private readonly LinkedList<(string Id, AIChatSession Session)> _sessions = new();
    private string? _signature;
    private bool _initialized;

    public AIHelpService(IAISettingsService settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised (on any thread) when readiness, the open context or the sessions change.</summary>
    public event Action? Changed;

    /// <summary>A cached copy of the active provider's <c>IsReadyForChat</c> (D5).</summary>
    public bool IsConfigured { get; private set; }

    /// <summary>What the panel is showing; null when it is closed.</summary>
    public AIHelpContext? Current { get; private set; }

    public bool IsOpen => Current is not null;

    /// <summary>Reads settings once; called from <c>MainLayout</c>.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;
        await RefreshAsync();
    }

    /// <summary>Re-reads settings (after a save, or when the Settings page opens, which covers hand edits).</summary>
    public async Task RefreshAsync()
    {
        bool ready;
        string signature;
        try
        {
            var active = (await _settings.GetAsync(CancellationToken.None)).Active;
            ready = active.IsReadyForChat;
            signature = $"{active.ServiceType}|{active.AIModel}|{active.Endpoint}|{active.ApiProtocol}";
        }
        catch (Exception)
        {
            // An unreadable settings file just means AI Help is off.
            (ready, signature) = (false, "");
        }

        lock (_gate)
        {
            // A different provider or model: old chats were with another model, so they are dropped.
            if (_signature is not null && _signature != signature)
                _sessions.Clear();
            _signature = signature;
        }
        IsConfigured = ready;
        if (!ready)
            Current = null;
        Changed?.Invoke();
    }

    public void Show(AIHelpContext context)
    {
        Current = context;
        Changed?.Invoke();
    }

    /// <summary>Closes the panel and keeps the session, so opening the same Id resumes the chat.</summary>
    public void Close()
    {
        Current = null;
        Changed?.Invoke();
    }

    public AIChatSession? GetSession(string id)
    {
        lock (_gate)
        {
            for (var node = _sessions.First; node is not null; node = node.Next)
            {
                if (node.Value.Id != id)
                    continue;
                _sessions.Remove(node);
                _sessions.AddFirst(node);
                return node.Value.Session;
            }
            return null;
        }
    }

    public void SetSession(string id, AIChatSession session)
    {
        lock (_gate)
        {
            RemoveLocked(id);
            _sessions.AddFirst((id, session));
            while (_sessions.Count > MaxSessions)
                _sessions.RemoveLast();
        }
    }

    /// <summary>New chat: drops the session for <paramref name="id"/>.</summary>
    public void DropSession(string id)
    {
        lock (_gate)
            RemoveLocked(id);
        Changed?.Invoke();
    }

    private void RemoveLocked(string id)
    {
        for (var node = _sessions.First; node is not null; node = node.Next)
        {
            if (node.Value.Id == id)
            {
                _sessions.Remove(node);
                return;
            }
        }
    }

    private void OnSettingsChanged() => _ = RefreshAsync();

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
