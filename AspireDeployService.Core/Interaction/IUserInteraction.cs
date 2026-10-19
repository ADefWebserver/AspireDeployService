namespace AspireDeployService.Core.Interaction;

/// <summary>Yes/no confirmations from Core to the user (implementation plan D6). The MAUI app shows a dialog.</summary>
public interface IUserInteraction
{
    /// <summary>
    /// Returns false when declined, or when <paramref name="ct"/> is canceled while the question is open.
    /// Services then end with OutcomeCode.Declined (or Canceled when <paramref name="ct"/> fired).
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken ct);
}
