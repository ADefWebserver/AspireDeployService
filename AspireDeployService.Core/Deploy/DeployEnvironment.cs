using AspireDeployService.Core.Discovery;
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

        foreach (var (name, value) in profile.Parameters)
        {
            if (value.Length == 0)
                continue; // "" = the AppHost's own configuration supplies it
            env[ToEnvironmentVariable(ConfigKeyFor(name, profile, discovered))] = value;
        }
        return env;
    }
}
