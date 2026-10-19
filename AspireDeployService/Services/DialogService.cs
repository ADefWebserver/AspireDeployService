using AspireDeployService.Core.Interaction;

namespace AspireDeployService.Services;

/// <summary>
/// Confirmations from Core, shown one at a time by <c>DialogHost</c> in the main layout (plan §5.2). A question
/// closes as "declined" when its token is canceled, so a canceled run never waits on an open dialog.
/// </summary>
public sealed class DialogService : IUserInteraction
{
    private readonly object _gate = new();
    private readonly LinkedList<DialogRequest> _queue = new();

    /// <summary>Raised (on any thread) when the current dialog changes.</summary>
    public event Action? Changed;

    public DialogRequest? Current
    {
        get
        {
            lock (_gate)
                return _queue.First?.Value;
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken ct)
    {
        var request = new DialogRequest(title, message, confirmLabel);
        lock (_gate)
            _queue.AddLast(request);
        Changed?.Invoke();

        await using var registration = ct.Register(() => Complete(request, false));
        return await request.Completion.Task;
    }

    public void Answer(DialogRequest request, bool confirmed) => Complete(request, confirmed);

    private void Complete(DialogRequest request, bool confirmed)
    {
        bool removed;
        lock (_gate)
            removed = _queue.Remove(request);
        request.Completion.TrySetResult(confirmed);
        if (removed)
            Changed?.Invoke();
    }
}

public sealed class DialogRequest(string title, string message, string confirmLabel)
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmLabel { get; } = confirmLabel;
    internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
