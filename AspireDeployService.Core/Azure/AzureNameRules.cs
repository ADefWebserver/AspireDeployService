using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AspireDeployService.Core.Settings;

// Rules copied from BlazorDataDeploymentWebsite.Core/Deployments/ResourceNameRules.cs (resource pickers plan D9).
// Changes: only these kinds, and registry names must be lowercase because Azure stores them that way.
namespace AspireDeployService.Core.Azure;

public enum AzureResourceKind { ResourceGroup, ContainerRegistry, ContainerAppEnvironment, ContainerApp, ManagedIdentity, LogAnalyticsWorkspace }

/// <summary>Azure naming rules for the resources the profile page can name (resource pickers plan §6.2).</summary>
public static partial class AzureNameRules
{
    [GeneratedRegex("^[a-z0-9]{5,50}$")]
    private static partial Regex RegistryPattern();

    [GeneratedRegex("^(?!.*--)[a-z][a-z0-9-]{0,58}[a-z0-9]$")]
    private static partial Regex EnvironmentPattern();

    [GeneratedRegex("^(?!.*--)[a-z][a-z0-9-]{0,30}[a-z0-9]$")]
    private static partial Regex ContainerAppPattern();

    public const string RegistryRule = "5 to 50 lowercase letters or digits, unique across Azure.";
    public const string EnvironmentRule = "2 to 60 lowercase letters, digits or single dashes, starting with a letter and not ending with a dash.";
    public const string ContainerAppRule = "2 to 32 lowercase letters, digits or single dashes, starting with a letter and not ending with a dash.";
    public const string ResourceGroupRule = "1 to 90 letters, digits, '-', '_', '.', '(' or ')', not ending with '.'.";

    /// <summary>null when valid, otherwise one sentence for the UI. Identities and workspaces are only picked, never named.</summary>
    public static string? Problem(AzureResourceKind kind, string? name)
    {
        if (kind is AzureResourceKind.ManagedIdentity or AzureResourceKind.LogAnalyticsWorkspace)
            return null;
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name.";
        return kind switch
        {
            AzureResourceKind.ContainerRegistry when !RegistryPattern().IsMatch(name) =>
                name.Any(char.IsUpper) ? "Use lowercase letters only: " + RegistryRule : "Registry names: " + RegistryRule,
            AzureResourceKind.ContainerAppEnvironment when !EnvironmentPattern().IsMatch(name) => "Environment names: " + EnvironmentRule,
            AzureResourceKind.ContainerApp when !ContainerAppPattern().IsMatch(name) => "Container app names: " + ContainerAppRule,
            AzureResourceKind.ResourceGroup when !ProfileRules.IsValidResourceGroup(name) => "Resource group names: " + ResourceGroupRule,
            _ => null
        };
    }

    /// <summary>
    /// A valid name derived from the profile name, without Azure access. A registry name shorter than 5 characters is
    /// padded with 4 hex characters; <paramref name="suffix"/> is appended when given (a taken name is retried with one).
    /// </summary>
    public static string Suggest(AzureResourceKind kind, string profileName, string? suffix = null)
    {
        var lower = (profileName ?? "").ToLowerInvariant();
        switch (kind)
        {
            case AzureResourceKind.ContainerRegistry:
            {
                var core = "cr" + new string(lower.Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());
                var tail = suffix ?? "";
                if (core.Length + tail.Length < 5)
                    tail += RandomHex();
                core = core[..Math.Min(core.Length, 50 - tail.Length)];
                return core + tail;
            }
            case AzureResourceKind.ContainerAppEnvironment:
                return Dashed("cae-", lower, suffix, 60);
            case AzureResourceKind.ContainerApp:
                return Dashed("", lower, suffix, 32);
            case AzureResourceKind.ResourceGroup:
                return Dashed("rg-", lower, suffix, 90);
            default:
                return lower;
        }
    }

    /// <summary>For the guide's generated naming table (§6.7).</summary>
    public static IReadOnlyList<(AzureResourceKind Kind, string Rule, bool GloballyUnique)> Describe() =>
    [
        (AzureResourceKind.ResourceGroup, ResourceGroupRule, false),
        (AzureResourceKind.ContainerRegistry, RegistryRule, true),
        (AzureResourceKind.ContainerAppEnvironment, EnvironmentRule, false),
        (AzureResourceKind.ContainerApp, ContainerAppRule, false),
    ];

    public static string Display(AzureResourceKind kind) => kind switch
    {
        AzureResourceKind.ResourceGroup => "Resource group",
        AzureResourceKind.ContainerRegistry => "Container registry",
        AzureResourceKind.ContainerAppEnvironment => "Container Apps environment",
        AzureResourceKind.ContainerApp => "Container app",
        AzureResourceKind.ManagedIdentity => "Managed identity",
        AzureResourceKind.LogAnalyticsWorkspace => "Log Analytics workspace",
        _ => kind.ToString()
    };

    private static string Dashed(string prefix, string lower, string? suffix, int max)
    {
        var sb = new StringBuilder();
        foreach (var c in lower)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var core = sb.ToString().Trim('-');
        if (core.Length == 0)
            core = "app";
        var tail = string.IsNullOrEmpty(suffix) ? "" : "-" + suffix;
        var name = prefix + core;
        name = name[..Math.Min(name.Length, max - tail.Length)].TrimEnd('-') + tail;
        if (!char.IsAsciiLetterLower(name[0]))
            name = ("a" + name)[..Math.Min(name.Length + 1, max)];
        return name;
    }

    private static string RandomHex() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2));
}
