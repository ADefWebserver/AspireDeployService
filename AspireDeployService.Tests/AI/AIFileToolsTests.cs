using System.Diagnostics;
using AspireDeployService.Core.AI;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;
using AspireDeployService.Core.Services;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests.AI;

public class AIFileToolsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _root;

    public AIFileToolsTests()
    {
        _root = _dir.Combine("src");
        _dir.Write("src/App/App.csproj", "<Project>\n  <PackageReference Include=\"Microsoft.Extensions.AI\" Version=\"10.10.0\" />\n</Project>");
        _dir.Write("src/App/appsettings.json", "{ \"Logging\": {} }");
        _dir.Write("src/App/appsettings.Development.json", "{ \"Secret\": \"x\" }");
        _dir.Write("src/App/local.settings.json", "{}");
        _dir.Write("src/App/bin/Debug/App.dll.txt", "PackageReference in bin");
        _dir.Write("src/App/obj/project.assets.json", "PackageReference in obj");
        _dir.Write("src/.git/config", "PackageReference in git");
        _dir.Write("outside.txt", "outside the root");
        File.WriteAllBytes(_dir.Combine("src", "App", "cert.pfx"), [1, 2, 3]);
    }

    public void Dispose() => _dir.Dispose();

    private AIFileTools Tools(string? log = null, params string[] denied) =>
        new(_root, log, denied, AIPromptScrubber.Create(new SettingsDocument(), "super-secret-value"));

    [Fact]
    public void Paths_outside_the_root_are_refused()
    {
        var tools = Tools();
        Assert.StartsWith("Refused: outside", tools.ReadFile("..\\outside.txt"));
        Assert.StartsWith("Refused: outside", tools.ReadFile("../outside.txt"));
        Assert.StartsWith("Refused: outside", tools.ReadFile(_dir.Combine("outside.txt")));
        Assert.StartsWith("Refused: outside", tools.ListFiles(".."));
        Assert.Contains("App.csproj", tools.ReadFile(Path.Combine(_root, "App", "App.csproj")));
    }

    [Fact]
    public void Skipped_folders_are_not_listed_searched_or_read()
    {
        var tools = Tools();
        var list = tools.ListFiles();
        Assert.Contains("App/App.csproj", list);
        Assert.DoesNotContain("bin", list);
        Assert.DoesNotContain("obj", list);
        Assert.DoesNotContain(".git", list);

        var search = tools.SearchFiles("PackageReference");
        Assert.Contains("App/App.csproj:2:", search);
        Assert.DoesNotContain("in bin", search);
        Assert.DoesNotContain("in obj", search);
        Assert.DoesNotContain("in git", search);

        Assert.StartsWith("Refused", tools.ReadFile("App/bin/Debug/App.dll.txt"));
    }

    [Fact]
    public void Secret_files_are_refused_but_appsettings_json_is_allowed()
    {
        var tools = Tools();
        Assert.StartsWith("Refused", tools.ReadFile("App/appsettings.Development.json"));
        Assert.StartsWith("Refused", tools.ReadFile("App/local.settings.json"));
        Assert.StartsWith("Refused", tools.ReadFile("App/cert.pfx"));
        Assert.Contains("Logging", tools.ReadFile("App/appsettings.json"));
    }

    [Fact]
    public void The_settings_file_is_refused_by_full_path_inside_the_root()
    {
        var settings = _dir.Write("src/settings.txt", "AspireDeploy");
        Assert.StartsWith("Refused: this is Aspire Deploy's settings file", Tools(null, settings).ReadFile("settings.txt"));
        Assert.DoesNotContain("AspireDeploy", Tools(null, settings).SearchFiles("AspireDeploy"));
    }

    [Fact]
    public void Large_and_binary_files_are_refused()
    {
        File.WriteAllText(Path.Combine(_root, "big.txt"), new string('a', 2 * 1024 * 1024));
        File.WriteAllBytes(Path.Combine(_root, "bin.dat"), [65, 0, 66]);
        var tools = Tools();
        Assert.Contains("larger than 1 MB", tools.ReadFile("big.txt"));
        Assert.Contains("binary", tools.ReadFile("bin.dat"));
    }

    [Fact]
    public void Max_lines_is_clamped_and_output_is_scrubbed()
    {
        File.WriteAllLines(Path.Combine(_root, "long.txt"), Enumerable.Range(1, 1000).Select(i => $"row {i} super-secret-value"));
        var text = Tools().ReadFile("long.txt", startLine: 1, maxLines: 5000);
        Assert.Contains("lines 1-400 of 1000", text);
        Assert.Contains("400: row 400", text);
        Assert.DoesNotContain("401: row 401", text);
        Assert.DoesNotContain("super-secret-value", text);
    }

    [Fact]
    public void Budget_stops_further_output()
    {
        File.WriteAllLines(Path.Combine(_root, "wide.txt"), Enumerable.Range(1, 400).Select(_ => new string('w', 75)));
        var tools = Tools();
        for (var i = 0; i < 4; i++)
            tools.ReadFile("wide.txt");
        Assert.Equal(AIFileTools.BudgetUsed, tools.ReadFile("App/App.csproj"));
    }

    [Fact]
    public void Tool_names_follow_the_contract()
    {
        var log = _dir.Write("run.log", "x");
        Assert.Equal(["list_files", "read_file", "search_files", "read_run_log"], Tools(log).AsTools().Select(t => t.Name));
        Assert.Equal(["read_run_log"], new AIFileTools(null, log, [], AIPromptScrubber.Create(new SettingsDocument())).AsTools().Select(t => t.Name));
    }

    [Fact]
    public async Task Run_log_is_read_while_another_stream_writes_it()
    {
        var log = _dir.Combine("live.log");
        await using var writer = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.Read);
        var bytes = "first line\nsecond line\n"u8.ToArray();
        await writer.WriteAsync(bytes);
        await writer.FlushAsync();
        Assert.Contains("2: second line", Tools(log).ReadRunLog());
    }

    [Fact]
    public void Junctions_are_refused_and_never_followed()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var target = _dir.Combine("secret-folder");
        _dir.Write("secret-folder/hidden.txt", "PackageReference hidden");
        var junction = Path.Combine(_root, "link");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mklink.WaitForExit();
        if (!Directory.Exists(junction))
            return;

        try
        {
            var tools = Tools();
            Assert.Contains("symbolic link or junction", tools.ReadFile("link/hidden.txt"));
            Assert.DoesNotContain("hidden", tools.ListFiles());
            Assert.DoesNotContain("hidden", tools.SearchFiles("PackageReference"));
        }
        finally
        {
            Directory.Delete(junction); // removes the link only, so TempDir can delete the rest
        }
    }
}

public class SourceReadyTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Local_source_reports_its_folder()
    {
        var local = new SourceSpec(SourceKind.LocalFolder, _h.SourceFolder, null, null);
        await _h.Discovery().DiscoverAsync(new DiscoverRequest(local, null, StaticOnly: true), _h.Observer, CancellationToken.None);
        Assert.Equal(Path.GetFullPath(_h.SourceFolder), Path.GetFullPath(Assert.Single(_h.Observer.SourceRoots)));
    }

    [Fact]
    public async Task GitHub_source_reports_the_extracted_folder()
    {
        _h.ServeRepository();
        await _h.Discovery().DiscoverAsync(new DiscoverRequest(new SourceSpec(SourceKind.GitHub, "owner/repo", "main", null), null, true),
            _h.Observer, CancellationToken.None);
        var root = Assert.Single(_h.Observer.SourceRoots);
        Assert.StartsWith(Path.GetFullPath(_h.WorkRoot), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(root, "Shop.AppHost", "AppHost.cs")));
    }
}
