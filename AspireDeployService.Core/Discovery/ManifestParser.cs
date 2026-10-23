using System.Text.Json;
using System.Text.RegularExpressions;
using AspireDeployService.Core.Outcomes;

namespace AspireDeployService.Core.Discovery;

public sealed record ManifestContents(IReadOnlyList<DiscoveredParameter> Parameters, IReadOnlyList<DiscoveredResource> Resources, IReadOnlyList<string> Warnings);

/// <summary>Turns <c>aspire-manifest.json</c> into parameters and resources (source plan §6.5 mapping table).</summary>
public static partial class ManifestParser
{
    /// <param name="manifestDirectory">The folder of the manifest, to read each compute resource's Bicep module for its
    /// container app name; null skips that.</param>
    public static ManifestContents Parse(string json, string? manifestDirectory = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new DeployerException(OutcomeCode.DiscoveryFailed, $"The Aspire manifest is not valid JSON: {ex.Message}", inner: ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("resources", out var resources)
                || resources.ValueKind != JsonValueKind.Object)
                throw new DeployerException(OutcomeCode.DiscoveryFailed, "The Aspire manifest has no \"resources\" object.");

            var parameters = new List<DiscoveredParameter>();
            var found = new List<DiscoveredResource>();
            var warnings = new List<string>();

            foreach (var resource in resources.EnumerateObject())
            {
                var name = resource.Name;
                var r = resource.Value;
                var type = String(r, "type") ?? "";
                switch (type)
                {
                    case "parameter.v0":
                        parameters.Add(ParseParameter(name, r));
                        break;
                    case "project.v0" or "project.v1":
                        // Projects are built into container images for Azure compute (assumed; plan T0b (d)).
                        found.Add(new DiscoveredResource(name, type, null, String(r, "path"), false, true)
                            { ContainerAppName = ContainerAppNameOf(r, manifestDirectory) });
                        break;
                    case "container.v0" or "container.v1":
                        found.Add(new DiscoveredResource(name, type, String(r, "image"), null, false, r.TryGetProperty("build", out _))
                            { ContainerAppName = ContainerAppNameOf(r, manifestDirectory) });
                        break;
                    case "dockerfile.v0":
                        found.Add(new DiscoveredResource(name, type, null, String(r, "path"), false, true)
                            { ContainerAppName = ContainerAppNameOf(r, manifestDirectory) });
                        break;
                    case "azure.bicep.v0" or "azure.bicep.v1":
                        found.Add(new DiscoveredResource(name, type, null, null, true, false));
                        break;
                    case "value.v0" or "annotated.string":
                        found.Add(new DiscoveredResource(name, type, null, null, false, false));
                        break;
                    case "executable.v0":
                        found.Add(new DiscoveredResource(name, type, null, null, false, false));
                        warnings.Add($"'{name}' is an executable; executables are not deployed.");
                        break;
                    default:
                        found.Add(new DiscoveredResource(name, type, null, null, false, false));
                        warnings.Add($"'{name}' has an unknown resource type '{type}'.");
                        break;
                }
            }

            return new ManifestContents(parameters, found, warnings);
        }
    }

    // resource web 'Microsoft.App/containerApps@2025-07-01' = {
    //   name: 'azinventory-web'
    [GeneratedRegex(@"resource\s+\w+\s+'Microsoft\.App/containerApps@[^']+'\s*=\s*\{\s*name:\s*'([^'$]+)'", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerAppNamePattern();

    /// <summary>The literal container app name in the resource's <c>deployment</c> Bicep module; null when absent or computed.</summary>
    internal static string? ContainerAppNameOf(JsonElement resource, string? manifestDirectory)
    {
        if (manifestDirectory is null || !resource.TryGetProperty("deployment", out var deployment) || deployment.ValueKind != JsonValueKind.Object
            || String(deployment, "path") is not { Length: > 0 } path)
            return null;
        try
        {
            var file = Path.GetFullPath(Path.Combine(manifestDirectory, path));
            if (!File.Exists(file))
                return null;
            var m = ContainerAppNamePattern().Match(File.ReadAllText(file));
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static DiscoveredParameter ParseParameter(string name, JsonElement r)
    {
        var secret = false;
        var hasDefault = false;
        var generated = false;
        if (r.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object
            && inputs.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            secret = value.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.True;
            if (value.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                hasDefault = d.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null;
                generated = d.TryGetProperty("generate", out var g) && g.ValueKind != JsonValueKind.Null;
            }
        }

        var configKey = r.TryGetProperty("connectionString", out _) ? $"ConnectionStrings:{name}" : $"Parameters:{name}";
        return new DiscoveredParameter(name, secret, hasDefault, generated, configKey);
    }

    private static string? String(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
