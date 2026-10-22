using System.Text.Json.Nodes;
using AspireDeployService.Core.Deploy;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests.Deploy;

[Collection("AspireDeployState")]
public class AspireDeployStateTests : IDisposable
{
    private readonly TempDir _dir = new();

    public AspireDeployStateTests() => AspireDeployState.RootOverride = _dir.Combine("deployments");

    public void Dispose()
    {
        AspireDeployState.RootOverride = null;
        _dir.Dispose();
    }

    [Fact]
    public void Folder_is_the_uppercase_sha256_of_the_lowercased_apphost_path()
    {
        // Matches the folder Aspire 13.6.2 created for the sample AppHost in the end-to-end test.
        var folder = AspireDeployState.FolderFor(@"C:\Users\webma\Source\Repos\AspireDeployService\SampleAspireProject\SampleAspireProject.AppHost\SampleAspireProject.AppHost.csproj");
        Assert.StartsWith("CDDFED96D7AE8972", Path.GetFileName(folder));
    }

    [Fact]
    public void Only_overlay_parameters_are_removed_from_both_state_files()
    {
        var appHost = _dir.Combine("src", "Shop.AppHost", "Shop.AppHost.csproj");
        var folder = AspireDeployState.FolderFor(appHost);
        Directory.CreateDirectory(folder);
        const string json = """
            { "Parameters:adsExistingEnvironmentName": "old-env", "Parameters:adsOverlayRunId": "x",
              "Parameters:apiKey": "keep", "Azure:ResourceGroup": "rg-shop" }
            """;
        File.WriteAllText(Path.Combine(folder, "production.json"), json);
        File.WriteAllText(Path.Combine(folder, "production.json.migration"), json);

        var removed = AspireDeployState.RemoveOverlayParameters(appHost, "Production");

        Assert.Equal(["Parameters:adsExistingEnvironmentName", "Parameters:adsOverlayRunId"], removed);
        foreach (var file in new[] { "production.json", "production.json.migration" })
        {
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, file)))!.AsObject();
            Assert.Equal(["Parameters:apiKey", "Azure:ResourceGroup"], doc.Select(p => p.Key));
        }
    }

    [Fact]
    public void The_migration_snapshot_is_scrubbed_too()
    {
        // Shape captured from Aspire 13.6.2: CurrentState and ClaimedSections are JSON held in strings.
        var appHost = _dir.Combine("src", "apphost.cs");
        var folder = AspireDeployState.FolderFor(appHost);
        Directory.CreateDirectory(folder);
        var snapshot = new JsonObject
        {
            ["ClaimedSections"] = new JsonArray("Azure", "Parameters:adsExistingEnvironmentName", "Parameters:apiKey").ToJsonString(),
            ["CurrentState"] = new JsonObject
            {
                ["Parameters"] = new JsonObject
                {
                    ["adsExistingEnvironmentName"] = new JsonObject { [""] = "old-env" },
                    ["apiKey"] = new JsonObject { [""] = "keep" }
                },
                ["Azure"] = new JsonObject { ["Location"] = "eastus2" }
            }.ToJsonString()
        };
        File.WriteAllText(Path.Combine(folder, "production.json.migration"), snapshot.ToJsonString());

        var removed = AspireDeployState.RemoveOverlayParameters(appHost, "Production");

        Assert.Equal(["Parameters:adsExistingEnvironmentName"], removed);
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "production.json.migration")))!;
        var state = JsonNode.Parse(doc["CurrentState"]!.GetValue<string>())!;
        Assert.Equal(["apiKey"], state["Parameters"]!.AsObject().Select(p => p.Key));
        Assert.Equal("eastus2", state["Azure"]!["Location"]!.GetValue<string>());
        var claimed = JsonNode.Parse(doc["ClaimedSections"]!.GetValue<string>())!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Equal(["Azure", "Parameters:apiKey"], claimed);
    }

    [Fact]
    public void A_cache_for_another_destination_loses_its_Azure_section_only()
    {
        var appHost = _dir.Combine("src", "Shop.AppHost", "Shop.AppHost.csproj");
        var folder = AspireDeployState.FolderFor(appHost);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "production.json"), """
            { "Parameters:apiKey": "keep", "Azure:SubscriptionId": "sub", "Azure:ResourceGroup": "rg-other", "Azure:Location": "eastus2",
              "Azure:Deployments:env:Id": "/x" }
            """);
        File.WriteAllText(Path.Combine(folder, "production.json.migration"), new JsonObject
        {
            ["ClaimedSections"] = new JsonArray("Azure", "Azure:Deployments:env", "Parameters:apiKey").ToJsonString(),
            ["CurrentState"] = new JsonObject { ["Azure"] = new JsonObject { ["ResourceGroup"] = "rg-other" }, ["Parameters"] = new JsonObject() }.ToJsonString()
        }.ToJsonString());

        Assert.Null(AspireDeployState.ResetForOtherDestination(appHost, "Production", "SUB", "RG-OTHER", "eastus2")); // same, any case
        Assert.Equal("rg-other in eastus2 (subscription sub)",
            AspireDeployState.ResetForOtherDestination(appHost, "Production", "sub", "rg-mine", "eastus2"));

        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "production.json")))!.AsObject();
        Assert.Equal(["Parameters:apiKey"], doc.Select(p => p.Key));
        var migration = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "production.json.migration")))!;
        Assert.Equal(["Parameters"], JsonNode.Parse(migration["CurrentState"]!.GetValue<string>())!.AsObject().Select(p => p.Key));
        Assert.Equal(["Parameters:apiKey"], JsonNode.Parse(migration["ClaimedSections"]!.GetValue<string>())!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void A_missing_state_folder_is_not_an_error()
    {
        Assert.Empty(AspireDeployState.RemoveOverlayParameters(_dir.Combine("nowhere", "apphost.cs"), "Production"));
    }
}
