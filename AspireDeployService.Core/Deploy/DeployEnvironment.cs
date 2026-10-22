using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Core.Deploy;

/// <summary>The environment block for <c>aspire deploy</c> (source plan §6.7 and D11, plan §6.6).</summary>
public static class DeployEnvironment
{
    /// <summary>
    /// "Parameters:region-name" → "Parameters__region_name"; "ConnectionStrings:my-db" → "ConnectionStrings__my-db";
    /// any other key only has ':' replaced. Dashes become '_' only in the parameter name of a "Parameters:" key.
    /// </summary>
    public static string ToEnvironmentVariable(string configKey)
    {
        const string prefix = "Parameters:";
        return configKey.StartsWith(prefix, StringComparison.Ordinal)
            ? "Parameters__" + configKey[prefix.Length..].Replace(':', '_').Replace('-', '_')
            : configKey.Replace(":", "__", StringComparison.Ordinal);
    }

    /// <summary>The configuration key a profile parameter is sent under (source plan D12).</summary>
    public static string ConfigKeyFor(string name, DeployProfile profile, IReadOnlyList<DiscoveredParameter> discovered) =>
        profile.ParameterConfigKeys.TryGetValue(name, out var custom) && custom.Length > 0 ? custom
        : discovered.FirstOrDefault(p => p.Name == name)?.ConfigKey ?? $"Parameters:{name}";

    // "AZURE_" (one underscore, case-insensitive) also covers the "Azure__" destination keys, and AZURE_CONFIG_DIR,
    // AZURE_CLIENT_SECRET and the other credential variables ProcessRunner strips from the inherited environment.
    private static readonly string[] ReservedPrefixes = ["AZURE_", "ASPIRE_", "DCP_", "DOTNET_", "OTEL_", "MSBuild", "NUGET_"];
    private static readonly string[] ReservedNames = ["PATH", "PATHEXT", "ComSpec", "SystemRoot", "TEMP", "TMP", "HOME", "USERPROFILE"];

    /// <summary>
    /// Environment variables that control the deployment target or the processes themselves. A parameter may not
    /// map onto one: an override such as <c>Azure:SubscriptionId</c> would deploy somewhere other than the
    /// destination the user confirmed.
    /// </summary>
    public static bool IsReserved(string environmentVariable) =>
        ReservedNames.Contains(environmentVariable, StringComparer.OrdinalIgnoreCase)
        || ReservedPrefixes.Concat(StrippedButNotParameters).Concat(OverlayPrefixes)
            .Any(p => environmentVariable.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // Resource pickers plan §6.4 "Reserved names": the overlay's own parameters (D12).
    private static readonly string[] OverlayPrefixes = ["Parameters__adsExisting", "Parameters__adsOverlay"];

    // Whatever ProcessRunner strips from the inherited environment (credentials, managed identity, MSBuild paths)
    // must not come back through a parameter either. Parameters__ and ConnectionStrings__ are what parameters are for.
    private static readonly string[] StrippedButNotParameters = ProcessRunner.StrippedPrefixes
        .Where(p => p is not ("Parameters__" or "ConnectionStrings__"))
        .ToArray();

    /// <summary>The parameters whose configuration key maps onto a reserved variable (empty when all are allowed).</summary>
    public static IReadOnlyList<string> ReservedParameterMappings(DeployProfile profile, IReadOnlyList<DiscoveredParameter> discovered) =>
        profile.Parameters.Keys
            .Concat(profile.ParameterConfigKeys.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(name => IsReserved(ToEnvironmentVariable(ConfigKeyFor(name, profile, discovered))))
            .Select(name => $"{name} → {ConfigKeyFor(name, profile, discovered)}")
            .ToList();

    public static Dictionary<string, string?> Build(DeployProfile profile, IReadOnlyList<DiscoveredParameter> discovered,
        string? aspireCliFolder, Func<string, string?> parentEnvironment)
    {
        var d = profile.Destination;
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Azure__SubscriptionId"] = d.SubscriptionId,
            ["Azure__Location"] = d.Location,
            ["Azure__ResourceGroup"] = d.ResourceGroup,
            ["Azure__CredentialSource"] = string.IsNullOrWhiteSpace(d.CredentialSource) ? "AzureCli" : d.CredentialSource,
            ["Azure__AllowResourceGroupCreation"] = (d.AllowResourceGroupCreation ?? true) ? "true" : "false",
            ["DOTNET_ENVIRONMENT"] = profile.EnvironmentName,
            ["ASPIRE_CLI_TELEMETRY_OPTOUT"] = "1",
            ["PATH"] = ToolPath.With(aspireCliFolder)
        };
        if (!string.IsNullOrWhiteSpace(d.TenantId))
            env["Azure__TenantId"] = d.TenantId;

        // ProcessRunner strips every inherited ASPIRE_* variable, so the one a developer needs is put back.
        var runtime = !string.IsNullOrWhiteSpace(profile.ContainerRuntime) ? profile.ContainerRuntime : parentEnvironment("ASPIRE_CONTAINER_RUNTIME");
        if (!string.IsNullOrWhiteSpace(runtime))
            env["ASPIRE_CONTAINER_RUNTIME"] = runtime;

        var reserved = ReservedParameterMappings(profile, discovered);
        if (reserved.Count > 0)
            throw new DeployerException(OutcomeCode.InvalidInput,
                "These parameters map onto settings that control the deployment itself, so they were refused: "
                + string.Join(", ", reserved) + ". Change their configuration key on the profile page.");

        foreach (var (name, value) in profile.Parameters)
        {
            if (value.Length == 0)
                continue; // "" = the AppHost's own configuration supplies it
            env[ToEnvironmentVariable(ConfigKeyFor(name, profile, discovered))] = value;
        }
        return env;
    }
}
