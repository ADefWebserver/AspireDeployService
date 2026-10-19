using System.Text.RegularExpressions;

// Copied from BlazorDataDeploymentWebsite.Core/Deployments/ErrorClassifier.cs (implementation plan D2, D10).
// The Azure SDK RequestFailedException and DeployManifestException cases are removed: this app makes no
// Azure SDK calls and classifies only text from aspire/az output.
namespace AspireDeployService.Core.Errors;

/// <summary>The category of a failed deployment plus a short, user-safe summary.</summary>
/// <param name="PolicyName">For <see cref="ErrorCategory.Policy"/>: the policy assignment or definition name when ARM reported it.</param>
public sealed record ErrorClassification(ErrorCategory Category, string Summary, string? PolicyName = null);

/// <summary>
/// A failure whose category is already known where it is thrown (for example an unsafe source archive). <see cref="ErrorClassifier"/> honours the category instead of pattern matching.
/// </summary>
public sealed class DeploymentFailureException(ErrorCategory category, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ErrorCategory Category { get; } = category;
}

/// <summary>
/// Maps deployment failures to <see cref="ErrorCategory"/>. Works on exceptions (timeouts, archive
/// errors) and on captured aspire / az output, because ARM errors reach this app only as text from a child process.
/// </summary>
public static partial class ErrorClassifier
{
    private const int MaxSummaryLength = 500;

    // Order matters: a policy denial or a missing provider registration is more specific than the
    // AuthorizationFailed text that sometimes accompanies it.
    private static readonly (ErrorCategory Category, Regex Pattern)[] Patterns =
    [
        (ErrorCategory.Policy, PolicyPattern()),
        (ErrorCategory.Provider, ProviderPattern()),
        (ErrorCategory.Quota, QuotaPattern()),
        (ErrorCategory.Naming, NamingPattern()),
        (ErrorCategory.Permissions, PermissionsPattern()),
        (ErrorCategory.Timeout, TimeoutPattern()),
    ];

    [GeneratedRegex(@"RequestDisallowedByPolicy|disallowed by policy", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyPattern();

    [GeneratedRegex(@"MissingSubscriptionRegistration|is not registered to use namespace|SubscriptionNotRegistered", RegexOptions.IgnoreCase)]
    private static partial Regex ProviderPattern();

    [GeneratedRegex(@"QuotaExceeded|MaxNumberOfRegionalEnvironmentsInSubExceeded|InsufficientQuota|SkuNotAvailable|exceeds? (the )?(approved )?quota", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaPattern();

    [GeneratedRegex(@"StorageAccountAlreadyTaken|NameNotAvailable|NameAlreadyExists|ServerNameAlreadyExists|RegistryNameNotAvailable|AlreadyExists\b|is already (taken|in use)|AccountNameInvalid|InvalidResourceName|ResourceNameInvalid", RegexOptions.IgnoreCase)]
    private static partial Regex NamingPattern();

    [GeneratedRegex(@"AuthorizationFailed|does not have authorization|AuthorizationPermissionMismatch|LinkedAuthorizationFailed|AADSTS700016|AADSTS7000215|AADSTS700213|AADSTS70021|AADSTS7000222", RegexOptions.IgnoreCase)]
    private static partial Regex PermissionsPattern();

    [GeneratedRegex(@"context deadline exceeded|DeploymentTimeout|operation timed out|timed out after", RegexOptions.IgnoreCase)]
    private static partial Regex TimeoutPattern();

    // ARM puts the policy identifiers in the error message as JSON:
    // Policy identifiers: '[{"policyAssignment":{"name":"Allowed locations","id":"..."},"policyDefinition":{...}}]'
    [GeneratedRegex(@"""policyAssignment""\s*:\s*\{\s*""name""\s*:\s*""(?<name>[^""]{1,200})""", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyAssignmentNamePattern();

    [GeneratedRegex(@"""policyDefinition""\s*:\s*\{\s*""name""\s*:\s*""(?<name>[^""]{1,200})""", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyDefinitionNamePattern();

    [GeneratedRegex(@"policy assignment (name\s*)?['""](?<name>[^'""]{1,200})['""]", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyTextNamePattern();

    /// <summary>Classifies an exception, optionally with the (already redacted) output of the process that failed.</summary>
    public static ErrorClassification Classify(Exception ex, string? output = null)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DeploymentFailureException known && known.Category != ErrorCategory.Unknown)
                return new ErrorClassification(known.Category, Trim(known.Message), ExtractPolicyName(known.Message + "\n" + output));
            if (e is InvalidDataException)
                return new ErrorClassification(ErrorCategory.Source, Trim(e.Message));
        }

        // Text first: ARM error codes in the message or the process output are more precise than HTTP status codes.
        var text = string.Join('\n', Messages(ex).Append(output ?? ""));
        if (ClassifyText(text) is { } fromText)
            return fromText;

        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is TimeoutException)
                return new(ErrorCategory.Timeout, Trim(e.Message));
        }

        return new ErrorClassification(ErrorCategory.Unknown, Trim(ex.Message));
    }

    /// <summary>Classifies captured output alone; null when no known pattern matches.</summary>
    public static ErrorClassification? ClassifyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var (category, pattern) in Patterns)
        {
            var match = pattern.Match(text);
            if (!match.Success)
                continue;

            var policy = category == ErrorCategory.Policy ? ExtractPolicyName(text) : null;
            var summary = category == ErrorCategory.Policy && policy is not null
                ? $"Blocked by Azure Policy '{policy}'."
                : Trim(LineAround(text, match.Index));
            return new ErrorClassification(category, summary, policy);
        }

        return null;
    }

    public static string? ExtractPolicyName(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        foreach (var pattern in new[] { PolicyAssignmentNamePattern(), PolicyDefinitionNamePattern(), PolicyTextNamePattern() })
        {
            var match = pattern.Match(text);
            if (match.Success)
                return match.Groups["name"].Value.Trim();
        }
        return null;
    }

    private static IEnumerable<string> Messages(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            yield return e.Message;
        }
    }

    private static string LineAround(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        return (end < 0 ? text[start..] : text[start..end]).Trim();
    }

    private static string Trim(string text)
    {
        text = text.Trim();
        return text.Length <= MaxSummaryLength ? text : text[..MaxSummaryLength] + "…";
    }
}
