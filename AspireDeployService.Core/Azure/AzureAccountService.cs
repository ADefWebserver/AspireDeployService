using System.ComponentModel;
using System.Text.Json;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Core.Azure;

public sealed record AzureSubscription(string Id, string Name, string TenantId, bool IsDefault);

public interface IAzureAccountService
{
    /// <summary>The subscriptions visible to the developer's <c>az login</c>. Code 3 when not signed in.</summary>
    Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(IRunObserver observer, CancellationToken ct);
}

public sealed class AzureAccountService(ProfileStore store, IRunContextFactory runs) : IAzureAccountService
{
    public async Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(IRunObserver observer, CancellationToken ct)
    {
        var options = (await store.LoadAsync(ct)).Options;
        await using var run = runs.Create(WorkKeys.For(options.WorkRoot, null, null), observer);
        ProcessResult result;
        try
        {
            var spec = new ProcessSpec("az", ["account", "list", "--output", "json"], Path.GetTempPath())
            {
                CaptureStdOut = true,
                LogStdOut = false,
                Timeout = TimeSpan.FromMinutes(2)
            };
            result = await run.Runner.RunAsync(spec, run.Log, ct);
        }
        catch (Win32Exception ex)
        {
            throw new DeployerException(OutcomeCode.MissingPrerequisite, "The Azure CLI (az) is not installed.", inner: ex);
        }

        if (!result.Succeeded)
            throw new DeployerException(OutcomeCode.MissingPrerequisite, "The Azure CLI is not signed in. Run: az login");

        try
        {
            using var doc = JsonDocument.Parse(string.Join('\n', result.StdOut));
            var list = doc.RootElement.EnumerateArray()
                .Select(s => new AzureSubscription(
                    s.GetProperty("id").GetString() ?? "",
                    s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    s.TryGetProperty("tenantId", out var t) ? t.GetString() ?? "" : "",
                    s.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True))
                .Where(s => s.Id.Length > 0)
                .OrderByDescending(s => s.IsDefault).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (list.Count == 0)
                throw new DeployerException(OutcomeCode.MissingPrerequisite, "The Azure CLI is signed in but sees no subscriptions. Run: az login");
            return list;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new DeployerException(OutcomeCode.MissingPrerequisite, "Could not read the output of az account list.", inner: ex);
        }
    }
}
