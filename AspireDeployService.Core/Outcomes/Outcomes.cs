using AspireDeployService.Core.Errors;

namespace AspireDeployService.Core.Outcomes;

/// <summary>
/// The result codes of every run (implementation plan D5). The numbers and meanings are the source plan's
/// exit codes, except that 5 also covers any canceled run.
/// </summary>
public enum OutcomeCode
{
    Success = 0,
    /// <summary>An unexpected error.</summary>
    Unexpected = 1,
    /// <summary>Invalid input or profile; no AppHost or several; work key locked; required parameter absent; GitHub repo, ref or token problem.</summary>
    InvalidInput = 2,
    /// <summary>A missing prerequisite: .NET SDK, az sign-in, container runtime, Node, or an Aspire CLI that cannot be installed.</summary>
    MissingPrerequisite = 3,
    /// <summary>Discovery failed or the archive is unsafe; no Azure target; submodules or LFS; files locked during source replacement.</summary>
    DiscoveryFailed = 4,
    /// <summary>A process failed, timed out, or the run was canceled.</summary>
    DeployFailed = 5,
    /// <summary>The Aspire version is below the minimum.</summary>
    UnsupportedVersion = 6,
    /// <summary>The user declined the trust prompt or the deploy confirmation.</summary>
    Declined = 7
}

public sealed record OperationResult(OutcomeCode Code, string Message,
    ErrorCategory Category = ErrorCategory.None, string? LogFile = null)
{
    public bool Succeeded => Code == OutcomeCode.Success;

    public static OperationResult Ok(string message, string? logFile = null) => new(OutcomeCode.Success, message, LogFile: logFile);
}

/// <summary>A failure with a known outcome code. Services throw it; RunCoordinator turns it into an <see cref="OperationResult"/>.</summary>
public sealed class DeployerException(OutcomeCode code, string message,
    ErrorCategory category = ErrorCategory.None, Exception? inner = null) : Exception(message, inner)
{
    public OutcomeCode Code { get; } = code;
    public ErrorCategory Category { get; } = category;
}
