using System.Text.Json;
using System.Text.RegularExpressions;
using AspireDeployService.Core.Errors;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Core.Azure;

/// <param name="Account">The signed-in user or service name from az's output, for "Signed in as …"; null when unreadable.</param>
public sealed record AzureSignInResult(string? Account, int SubscriptionCount);

public interface IAzureSignInService
{
    /// <summary>
    /// Runs <c>az login</c> in its own run context (the setup work key, like the list calls), so the observer receives the
    /// log, including the device-code line. Throws <see cref="DeployerException"/> (MissingPrerequisite) when az is missing,
    /// login fails or times out. Cancellation kills the process tree and surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<AzureSignInResult> SignInAsync(string? tenantId, bool useDeviceCode, IRunObserver observer, CancellationToken ct);
}

/// <summary>
/// Signs the developer in with <c>az login</c> (wizard plan D6, §6.3). The app passes no secret and reads none: az opens
/// the browser (or prints a device code) and keeps its own token cache.
/// </summary>
public sealed partial class AzureSignInService(ProfileStore store, IRunContextFactory runs, IAzureAccountService account) : IAzureSignInService
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>So az does not wait for a subscription choice on a closed stdin (wizard plan §6.3, assumed until T0).</summary>
    internal static readonly IReadOnlyDictionary<string, string?> Environment =
        new Dictionary<string, string?> { ["AZURE_CORE_LOGIN_EXPERIENCE_V2"] = "off" };

    [GeneratedRegex("^[A-Za-z0-9.-]{1,253}$")]
    private static partial Regex TenantDomainPattern();

    public async Task<AzureSignInResult> SignInAsync(string? tenantId, bool useDeviceCode, IRunObserver observer, CancellationToken ct)
    {
        var args = Arguments(tenantId, useDeviceCode);
        var options = (await store.LoadAsync(ct)).Options;
        await using var run = runs.Create(WorkKeys.For(options.WorkRoot, null, null), observer);

        AzResult result;
        try
        {
            result = await AzCli.RunAsync(args, run, Timeout, ct, Environment);
        }
        catch (TimeoutException ex)
        {
            throw new DeployerException(OutcomeCode.MissingPrerequisite,
                "Sign-in timed out after 5 minutes. Try again, or tick Use a device code.", ErrorCategory.Timeout, ex);
        }

        if (!result.Succeeded)
            throw new DeployerException(OutcomeCode.MissingPrerequisite, $"Sign-in did not finish: {result.ErrorLine}", ErrorCategory.Permissions);

        var signedIn = Parse(result.StdOut);
        account.InvalidateAll();
        await run.Log.InfoAsync(signedIn.Account is { } name
            ? $"Signed in as {name} ({signedIn.SubscriptionCount} subscriptions)."
            : $"Signed in ({signedIn.SubscriptionCount} subscriptions).", ct);
        return signedIn;
    }

    /// <summary><c>login [--tenant t] [--use-device-code] --output json</c>; an invalid tenant is <c>InvalidInput</c>.</summary>
    internal static IReadOnlyList<string> Arguments(string? tenantId, bool useDeviceCode)
    {
        List<string> args = ["login"];
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            var tenant = tenantId.Trim();
            if (!Guid.TryParse(tenant, out _) && !TenantDomainPattern().IsMatch(tenant))
                throw new DeployerException(OutcomeCode.InvalidInput, "The tenant must be a tenant ID (a GUID) or a domain such as contoso.onmicrosoft.com.");
            args.AddRange(["--tenant", tenant]);
        }
        if (useDeviceCode)
            args.Add("--use-device-code");
        args.AddRange(["--output", "json"]);
        return args;
    }

    /// <summary>az login prints the subscriptions it can see, each with a <c>user.name</c>.</summary>
    internal static AzureSignInResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new AzureSignInResult(null, 0);
            string? name = null;
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (s.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                    && user.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } value)
                {
                    name = value;
                    break;
                }
            }
            return new AzureSignInResult(name, doc.RootElement.GetArrayLength());
        }
        catch (JsonException)
        {
            return new AzureSignInResult(null, 0);
        }
    }
}
