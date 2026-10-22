using AspireDeployService.Core.Azure;
using AspireDeployService.Core.Discovery;

namespace AspireDeployService.Core.Deploy;

public enum AppPlanAction { New, Update, Conflict, Unknown }

public sealed record AppPlanRow(string Resource, string AppName, AppPlanAction Action, string? Note);

/// <param name="EnvironmentId">The environment the apps will run in, or null when it is not known yet
/// (AspireDefault on a first deploy).</param>
/// <param name="AppsInGroup">Apps in the profile's resource group; null when they could not be read.</param>
/// <param name="AppsInEnvironment">Apps anywhere in the subscription whose environment is EnvironmentId; empty when
/// EnvironmentId is null; null when they could not be read.</param>
public sealed record AppPlanInput(string ProfileResourceGroup, string? EnvironmentId,
    IReadOnlyList<AzureContainerApp>? AppsInGroup, IReadOnlyList<AzureContainerApp>? AppsInEnvironment);

/// <summary>The container apps preview (resource pickers plan §6.6, F3): which compute resources are new or updates.</summary>
public static class ContainerAppPlanner
{
    public static bool IsCompute(DiscoveredResource r) =>
        r.Type.StartsWith("project.", StringComparison.Ordinal)
        || r.Type.StartsWith("container.", StringComparison.Ordinal)
        || r.Type.StartsWith("dockerfile.", StringComparison.Ordinal);

    /// <param name="compute">Project, container or dockerfile resources; others are skipped.</param>
    public static IReadOnlyList<AppPlanRow> Plan(IReadOnlyList<DiscoveredResource> compute, AppPlanInput input)
    {
        var rows = new List<AppPlanRow>();
        foreach (var resource in compute.Where(IsCompute))
        {
            // Aspire names each container app after its AppHost resource, lowercased (T0 confirmed on 13.6.2).
            var app = resource.Name.ToLowerInvariant();
            rows.Add(Row(resource.Name, app, input));
        }
        return rows;
    }

    private static AppPlanRow Row(string resource, string app, AppPlanInput input)
    {
        if (AzureNameRules.Problem(AzureResourceKind.ContainerApp, app) is not null)
            return new(resource, app, AppPlanAction.Conflict, "Aspire will shorten or reject this name.");
        if (input.AppsInGroup is null || input.AppsInEnvironment is null)
            return new(resource, app, AppPlanAction.Unknown, "Could not read the existing apps.");

        var inGroup = input.AppsInGroup.FirstOrDefault(a => Same(a.Name, app)
            && Same(a.ResourceGroup, input.ProfileResourceGroup));
        if (inGroup is not null)
        {
            if (input.EnvironmentId is null || Same(inGroup.EnvironmentId, input.EnvironmentId))
                return new(resource, app, AppPlanAction.Update, null);
            return new(resource, app, AppPlanAction.Conflict,
                $"{app} already runs in environment {LastSegment(inGroup.EnvironmentId)}. An app cannot move to another environment; "
                + "delete it or use another resource group.");
        }

        var inEnvironment = input.AppsInEnvironment.FirstOrDefault(a => Same(a.Name, app)
            && Same(a.EnvironmentId, input.EnvironmentId) && !Same(a.ResourceGroup, input.ProfileResourceGroup));
        if (inEnvironment is not null)
            return new(resource, app, AppPlanAction.Conflict,
                $"{app} in {inEnvironment.ResourceGroup} already uses this name in the shared environment.");

        return new(resource, app, AppPlanAction.New, null);
    }

    public static string Describe(AppPlanRow row) => row.Action switch
    {
        AppPlanAction.New => $"{row.AppName} (new)",
        AppPlanAction.Update => $"{row.AppName} (update)",
        AppPlanAction.Unknown => $"{row.AppName} (could not check)",
        _ => $"{row.AppName} (conflict)"
    };

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string LastSegment(string? id) => id is null ? "(unknown)" : id[(id.LastIndexOf('/') + 1)..];
}
