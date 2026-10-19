using System.Text.Json;
using AspireDeployService.Core.Outcomes;

namespace AspireDeployService.Core.Discovery;

public sealed record ManifestContents(IReadOnlyList<DiscoveredParameter> Parameters, IReadOnlyList<DiscoveredResource> Resources, IReadOnlyList<string> Warnings);

/// <summary>Turns <c>aspire-manifest.json</c> into parameters and resources (source plan §6.5 mapping table).</summary>
public static class ManifestParser
{
    public static ManifestContents Parse(string json)
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
                        found.Add(new DiscoveredResource(name, type, null, String(r, "path"), false, true));
                        break;
                    case "container.v0" or "container.v1":
                        found.Add(new DiscoveredResource(name, type, String(r, "image"), null, false, r.TryGetProperty("build", out _)));
                        break;
                    case "dockerfile.v0":
                        found.Add(new DiscoveredResource(name, type, null, String(r, "path"), false, true));
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
