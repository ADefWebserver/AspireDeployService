using System.ComponentModel;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;

namespace AspireDeployService.Core.Azure;

public enum AzErrorKind { None, NotSignedIn, NotFound, ResourceGroupNotFound, Unauthorized, ProviderNotRegistered, Conflict, Other }

/// <param name="ErrorLine">The CLI's own "ERROR: ..." line, or the last output line; "" on success.</param>
public sealed record AzResult(int ExitCode, string StdOut, string ErrorLine, AzErrorKind Kind)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs <c>az</c> in a given run, so its lines reach that run's log (resource pickers plan §6.3). Arguments are a list,
/// never a shell string, and stdout is captured but not logged. A non-zero exit is returned, not thrown.
/// </summary>
internal static class AzCli
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Throws <see cref="DeployerException"/> (MissingPrerequisite) only when az is not installed.</summary>
    /// <param name="environment">Overrides for the child environment (wizard plan §6.3); a null value removes the variable.</param>
    /// <param name="log">Where the lines go instead of the run's own log (a filter around it); null = the run's log.</param>
    public static async Task<AzResult> RunAsync(IReadOnlyList<string> args, RunContext run, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string?>? environment = null, IDeploymentLogSink? log = null)
    {
        ProcessResult result;
        try
        {
            var spec = new ProcessSpec("az", args, Path.GetTempPath())
            {
                CaptureStdOut = true,
                LogStdOut = false,
                Timeout = timeout,
                Environment = environment ?? new Dictionary<string, string?>()
            };
            result = await run.Runner.RunAsync(spec, log ?? run.Log, ct);
        }
        catch (Win32Exception ex)
        {
            throw new DeployerException(OutcomeCode.MissingPrerequisite, "The Azure CLI (az) is not installed.", inner: ex);
        }

        var stdout = string.Join('\n', result.StdOut);
        if (result.Succeeded)
            return new AzResult(0, stdout, "", AzErrorKind.None);
        var line = ErrorLine(result.OutputTail);
        return new AzResult(result.ExitCode, stdout, line, Classify(line));
    }

    /// <summary>The words of the command before its first option, for messages: "az containerapp env list".</summary>
    public static string Describe(IReadOnlyList<string> args) =>
        "az " + string.Join(' ', args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)));

    internal static AzErrorKind Classify(string errorLine)
    {
        if (string.IsNullOrWhiteSpace(errorLine))
            return AzErrorKind.Other;
        bool Has(string s) => errorLine.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("ResourceGroupNotFound"))
            return AzErrorKind.ResourceGroupNotFound;
        if (Has("ResourceNotFound") || Has("was not found") || Has("could not be found"))
            return AzErrorKind.NotFound;
        if (Has("AuthorizationFailed") || Has("does not have authorization") || Has("LinkedAuthorizationFailed"))
            return AzErrorKind.Unauthorized;
        if (Has("MissingSubscriptionRegistration") || Has("is not registered to use namespace"))
            return AzErrorKind.ProviderNotRegistered;
        if (Has("Conflict") || Has("AlreadyExists") || Has("already in use") || Has("already exists"))
            return AzErrorKind.Conflict;
        // The Windows broker (WAM) reports an expired sign-in as "Account has previously been signed out … Status_AccountUnusable".
        if (Has("az login") || Has("AADSTS") || Has("Please run 'az login'") || Has("previously been signed out") || Has("AccountUnusable"))
            return AzErrorKind.NotSignedIn;
        return AzErrorKind.Other;
    }

    /// <summary>The CLI's own "ERROR: ..." line when there is one; az prints help text after it.</summary>
    internal static string ErrorLine(string outputTail)
    {
        var lines = outputTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? "see the log";
    }

    public const string ProviderFix = "Run: az provider register --namespace Microsoft.App";
}
