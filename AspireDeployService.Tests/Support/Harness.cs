using System.IO.Compression;
using System.Net;
using System.Text.Json;
using AspireDeployService.Core.Azure;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Prerequisites;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Services;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;
using AspireDeployService.Core.Tools;

namespace AspireDeployService.Tests.Support;

/// <summary>
/// Real Core services over a temp folder, a fake process runner and stubbed GitHub. The fake runner answers
/// <c>aspire --version</c> with <see cref="CliVersion"/>, writes the starter manifest for
/// <c>aspire do publish-manifest</c>, and hands <c>aspire deploy</c> to <see cref="OnDeploy"/>.
/// </summary>
public sealed class Harness : IDisposable
{
    public const string Sha = "0123456789abcdef0123456789abcdef01234567";

    public TempDir Dir { get; } = new();
    public string SettingsPath { get; }
    public string WorkRoot => Dir.Combine("w");
    public string SourceFolder => Dir.Combine("src");
    public string CliVersion { get; set; } = "13.6.1";
    public Func<ProcessSpec, CancellationToken, Task<ProcessResult>> OnDeploy { get; set; } =
        (_, _) => Task.FromResult(FakeProcessRunner.Ok("Deployment completed.", "webfrontend: https://web.example"));

    public FakeProcessRunner Runner { get; }
    public FakeRunContextFactory Runs { get; }
    public FakeUserInteraction Interaction { get; set; } = new();
    public StubHttpHandler GitHub { get; } = new();
    public ProfileStore Store { get; }
    public ProfileService Profiles { get; }
    public ListRunObserver Observer { get; } = new();

    public Harness()
    {
        SettingsPath = Dir.Combine("appsettings.Local.json");
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
        {
            AspireDeploy = new { WorkRoot, ToolsRoot = Dir.Combine("tools"), Profiles = new { } }
        }));
        Store = new ProfileStore(new FixedLocator(SettingsPath));
        Profiles = new ProfileService(Store);
        Runner = new FakeProcessRunner(HandleAsync);
        Runs = new FakeRunContextFactory(Runner);
        AppHostFixtures.ProjectAppHost(Dir, "src/Shop.AppHost", "13.6.1");
    }

    public DiscoveryPipeline Pipeline() => new(new StubHttpClientFactory(GitHub), new GitHubTokenResolver(), new TrustStore(),
        new AspireCliProvisioner(), Interaction);

    /// <summary>Answers <c>az</c> commands (the resource pickers plan's reads and creates). Null result = not handled.</summary>
    public Func<ProcessSpec, ProcessResult?> OnAz { get; set; } = _ => null;

    public IReadOnlyList<ProcessSpec> AzSpecs => Runner.Specs.Where(s => s.FileName == "az").ToList();

    public AzureAccountService Account() => new(Store, Runs, TimeProvider.System);

    public DeployService Deploy() => new(Store, Profiles, Runs, Pipeline(), new PrerequisiteChecker(), Interaction,
        new AzureResourceProvisioner(Store, Account(), Runs), TimeProvider.System)
    {
        SkipPrerequisites = true,
        ParentEnvironment = _ => null
    };

    public DiscoveryService Discovery() => new(Store, Runs, Pipeline());

    public async Task<DeployProfile> AddProfileAsync(string name = "shop", Action<DeployProfile>? change = null)
    {
        var p = new DeployProfile
        {
            Source = new ProfileSource { Kind = SourceKind.LocalFolder, Location = SourceFolder },
            Destination = new ProfileDestination
            {
                SubscriptionId = "00000000-0000-0000-0000-000000000001",
                Location = "eastus2",
                ResourceGroup = "rg-shop"
            },
            Parameters = { ["apiKey"] = "abc12", ["region-name"] = "west", ["db"] = "Server=db;" + "Pass" + "word=hunter2-long" },
            SecretParameters = ["apiKey", "db"]
        };
        change?.Invoke(p);
        await Profiles.SaveAsync(name, p, overwrite: true, CancellationToken.None);
        return p;
    }

    public IReadOnlyList<ProcessSpec> DeploySpecs => Runner.Specs.Where(s => FakeProcessRunner.Is(s, "deploy")).ToList();

    /// <summary>Serves the commit lookup and a zipball of the fixture AppHost for <c>owner/repo</c>.</summary>
    public void ServeRepository()
    {
        GitHub.On("https://api.github.com/repos/owner/repo/commits/", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{Sha}\"}}") });
        GitHub.On("https://api.github.com/repos/owner/repo/zipball/", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(RepositoryZip()) });
    }

    public static byte[] RepositoryZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry("owner-repo-0123456/" + path).Open());
                w.Write(text);
            }
            Add("Shop.AppHost/Shop.AppHost.csproj", """
                <Project Sdk="Aspire.AppHost.Sdk/13.6.1"><ItemGroup><PackageReference Include="Aspire.Hosting.Azure.AppContainers" Version="13.6.1" /></ItemGroup></Project>
                """);
            Add("Shop.AppHost/AppHost.cs", AppHostFixtures.AcaAppHostCs);
        }
        return ms.ToArray();
    }

    private async Task<ProcessResult> HandleAsync(ProcessSpec spec, CancellationToken ct)
    {
        if (spec.FileName == "az")
            return OnAz(spec) ?? FakeProcessRunner.Fail(1, "ERROR: (ResourceGroupNotFound) Resource group 'x' could not be found.");
        if (FakeProcessRunner.Is(spec, "--version"))
            return FakeProcessRunner.Ok(CliVersion + "+abcdef");
        if (FakeProcessRunner.Is(spec, "do", "publish-manifest"))
        {
            var output = spec.Arguments[spec.Arguments.ToList().IndexOf("--output-path") + 1];
            File.Copy(AppHostFixtures.Fixture("manifests", "starter-13.6.1.json"), output, overwrite: true);
            return FakeProcessRunner.Ok("Pipeline succeeded");
        }
        if (FakeProcessRunner.Is(spec, "deploy"))
            return await OnDeploy(spec, ct);
        if (FakeProcessRunner.Is(spec, "tool", "install"))
        {
            var dir = spec.Arguments[spec.Arguments.ToList().IndexOf("--tool-path") + 1];
            File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows() ? "aspire.cmd" : "aspire"), "");
            return FakeProcessRunner.Ok("installed");
        }
        if (FakeProcessRunner.Is(spec, "auth", "token"))
            return FakeProcessRunner.Fail(1, "not logged in");
        return FakeProcessRunner.Fail(127, $"unexpected command {ProcessRunner.FormatCommand(spec)}");
    }

    public bool WorkKeyIsFree(string name)
    {
        try
        {
            WorkKeyLock.Acquire(WorkKeys.For(WorkRoot, name, null), name).DisposeAsync().AsTask().Wait();
            return true;
        }
        catch (Core.Outcomes.DeployerException)
        {
            return false;
        }
    }

    public void Dispose() => Dir.Dispose();
}
