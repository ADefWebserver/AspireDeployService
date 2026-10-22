using System.Xml.Linq;
using AspireDeployService.Core.Deploy;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests.Deploy;

/// <summary>Resource pickers plan §6.4 (F2, D2, D3, D12).</summary>
public class AppHostOverlayTests : IDisposable
{
    private const string Original = @"C:\src\Shop.AppHost\AppHost.cs";
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string Fixture(string name) => File.ReadAllText(AppHostFixtures.Fixture("apphosts", name));

    // --- Patch ---

    [Fact]
    public void Patch_inserts_before_Build_with_line_directives()
    {
        var patched = AppHostOverlay.Patch(Fixture("toplevel.cs"), Original)!;
        var lines = patched.Replace("\r\n", "\n").Split('\n');
        var i = Array.IndexOf(lines, "#line hidden");
        Assert.Equal("AspireDeployOverrides.Apply(builder);", lines[i + 1]);
        Assert.Equal($"#line 7 \"{Original}\"", lines[i + 2]);
        Assert.Equal("builder.Build().Run();", lines[i + 3]);
        // Everything else is unchanged.
        Assert.Equal(Fixture("toplevel.cs").Replace("\r\n", "\n"), string.Join('\n', lines.Where((_, n) => n < i || n > i + 2)));
    }

    [Fact]
    public void Patch_handles_a_Main_method_a_typed_declaration_a_renamed_variable_and_keeps_indentation()
    {
        var lines = AppHostOverlay.Patch(Fixture("main-method.cs"), Original)!.Replace("\r\n", "\n").Split('\n');
        var i = Array.IndexOf(lines, "#line hidden");
        Assert.Equal("        AspireDeployOverrides.Apply(app);", lines[i + 1]);
        Assert.Equal($"#line 7 \"{Original}\"", lines[i + 2]);
        Assert.Equal("        var host = app", lines[i + 3]);
    }

    [Fact]
    public void Patch_keeps_CRLF_line_endings()
    {
        var patched = AppHostOverlay.Patch(Fixture("toplevel.cs").Replace("\r\n", "\n").Replace("\n", "\r\n"), Original)!;
        Assert.Contains("#line hidden\r\nAspireDeployOverrides.Apply(builder);\r\n", patched);
    }

    [Theory]
    [InlineData("two-builds.cs")]
    [InlineData("no-builder.cs")]
    public void Patch_gives_null_when_not_found_exactly_once(string fixture) =>
        Assert.Null(AppHostOverlay.Patch(Fixture(fixture), Original));

    [Fact]
    public void Patch_on_a_file_based_AppHost_keeps_the_directives_first()
    {
        var patched = AppHostOverlay.Patch(Fixture("filebased.cs"), Original)!;
        Assert.StartsWith("#:sdk Aspire.AppHost.Sdk@13.6.1\n#:package", patched.Replace("\r\n", "\n"));
    }

    // --- PatchTypeScript ---

    private OverlayRequest Request(AppHostInfo info, string? env = "cae-shared", string? registry = "crshared01", string? identity = null,
        string? appHostResource = null, IReadOnlyCollection<string>? resources = null) =>
        new(_dir.Path, info, _dir.Combine("work"), "run-1", resources ?? ["env", "api"], appHostResource,
            env, env is null ? null : "rg-shared", registry, registry is null ? null : "rg-shared",
            identity, identity is null ? null : "rg-shared");

    private static AppHostInfo Info(string path, AppHostKind kind, params string[] environments) =>
        new(path, kind, "13.6.1", ["Aspire.Hosting.Azure.AppContainers"], [DeploymentTarget.AzureContainerApps], null)
        {
            ContainerAppEnvironmentNames = environments
        };

    [Fact]
    public void PatchTypeScript_inserts_after_the_environment_statement()
    {
        var req = Request(Info("apphost.mts", AppHostKind.TypeScript, "env"), identity: "id-pull");
        var patched = AppHostOverlay.PatchTypeScript(Fixture("apphost.mts"), req)!;
        var envLine = patched.IndexOf("const env = await builder.addAzureContainerAppEnvironment(\"env\");", StringComparison.Ordinal);
        var inserted = patched.IndexOf("await env.publishAsExisting(await builder.addParameter(\"adsExistingEnvironmentName\"), "
            + "{ resourceGroup: await builder.addParameter(\"adsExistingEnvironmentResourceGroup\") });", StringComparison.Ordinal);
        Assert.True(envLine >= 0 && inserted > envLine);
        Assert.True(inserted < patched.IndexOf("const app", StringComparison.Ordinal));
        Assert.Contains("const adsRegistry = await builder.addAzureContainerRegistry(\"ads-existing-registry\");", patched);
        Assert.Contains("await env.withAzureContainerRegistry(adsRegistry);", patched);
        Assert.Contains("await env.withAcrPullIdentity(adsPullIdentity);", patched);
        Assert.Contains("Parameters__adsOverlayMarkerFile", patched);
        Assert.DoesNotContain("cae-shared", patched); // names travel as parameters only (D12)
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void PatchTypeScript_adds_only_the_chosen_calls(bool env, bool registry, bool identity)
    {
        var req = Request(Info("apphost.mts", AppHostKind.TypeScript, "env"),
            env ? "cae-shared" : null, registry ? "crshared01" : null, identity ? "id-pull" : null);
        var patched = AppHostOverlay.PatchTypeScript(Fixture("apphost.mts"), req)!;
        Assert.Equal(env, patched.Contains("env.publishAsExisting", StringComparison.Ordinal));
        Assert.Equal(registry, patched.Contains("addAzureContainerRegistry", StringComparison.Ordinal));
        Assert.Equal(identity, patched.Contains("addAzureUserAssignedIdentity", StringComparison.Ordinal));
    }

    [Fact]
    public void PatchTypeScript_gives_null_for_two_environments_or_none()
    {
        Assert.Null(AppHostOverlay.PatchTypeScript(Fixture("two-envs.mts"), Request(Info("a.mts", AppHostKind.TypeScript, "a", "b"))));
        Assert.Null(AppHostOverlay.PatchTypeScript("const builder = await createBuilder();\nawait builder.build().run();\n",
            Request(Info("a.mts", AppHostKind.TypeScript))));
    }

    [Fact]
    public void PatchTypeScript_picks_the_named_environment_and_ends_after_a_chained_statement()
    {
        var patched = AppHostOverlay.PatchTypeScript(Fixture("two-envs.mts"),
            Request(Info("a.mts", AppHostKind.TypeScript, "a", "b"), appHostResource: "b"))!;
        var chainEnd = patched.IndexOf(".withAzureContainerRegistry(x);", StringComparison.Ordinal);
        var inserted = patched.IndexOf("await b.publishAsExisting", StringComparison.Ordinal);
        Assert.True(chainEnd > 0 && inserted > chainEnd);
        Assert.DoesNotContain("await a.publishAsExisting", patched);
    }

    // --- Generated class and targets ---

    [Fact]
    public void Generated_class_uses_parameters_and_optional_sections()
    {
        var text = AppHostOverlay.GeneratedClass(Request(Info("x.csproj", AppHostKind.ProjectCsproj, "env"), registry: null));
        Assert.Contains("AddParameter(builder, \"adsExistingEnvironmentName\")", text);
        Assert.Contains("if (!builder.ExecutionContext.IsPublishMode)", text);
        Assert.DoesNotContain("AddAzureContainerRegistry", text);
        Assert.DoesNotContain("WithAcrPullIdentity", text);
        Assert.DoesNotContain("cae-shared", text);

        var withAll = AppHostOverlay.GeneratedClass(Request(Info("x.csproj", AppHostKind.ProjectCsproj, "a", "b"), identity: "id-pull", appHostResource: "b"));
        Assert.Contains("AddAzureContainerRegistry(builder, \"ads-existing-registry\")", withAll);
        Assert.Contains("WithAcrPullIdentity(environment, identity)", withAll);
        Assert.Contains("e.Name == \"b\"", withAll);
    }

    [Fact]
    public void Generated_class_with_only_a_registry_does_not_mark_the_environment_existing()
    {
        var text = AppHostOverlay.GeneratedClass(Request(Info("x.csproj", AppHostKind.ProjectCsproj, "env"), env: null));
        Assert.DoesNotContain("adsExistingEnvironmentName", text);
        Assert.Contains("WithAzureContainerRegistry(environment, registry)", text);
    }

    [Fact]
    public void Paths_with_special_characters_are_escaped_and_the_targets_file_parses()
    {
        var weird = @"C:\Users\O'Brien\R&D\100%;$x@y\Shop.AppHost.csproj";
        var escaped = AppHostOverlay.Escape(weird);
        Assert.Equal(@"C:\Users\O%27Brien\R&amp;D\100%25%3B%24x%40y\Shop.AppHost.csproj", escaped);

        var xml = AppHostOverlay.Targets(weird, "AppHost.cs", @"C:\w\O'Brien\overlay\AppHost.cs", @"C:\w\O'Brien\overlay\AspireDeployOverrides.g.cs");
        var doc = XDocument.Parse(xml);
        var group = doc.Root!.Element("ItemGroup")!;
        Assert.Equal(@"'$(MSBuildProjectFullPath)' == 'C:\Users\O%27Brien\R&D\100%25%3B%24x%40y\Shop.AppHost.csproj'",
            group.Attribute("Condition")!.Value);
        Assert.Equal("AppHost.cs", group.Elements("Compile").First().Attribute("Remove")!.Value);
    }

    // --- Prepare ---

    private AppHostInfo ProjectAppHost(string code = AppHostFixtures.AcaAppHostCs, params string[] environments)
    {
        AppHostFixtures.ProjectAppHost(_dir, "src/Shop.AppHost", "13.6.1", code);
        return Info("src/Shop.AppHost/Shop.AppHost.csproj", AppHostKind.ProjectCsproj, environments.Length == 0 ? ["env"] : environments);
    }

    [Fact]
    public void Prepare_project_writes_three_files_and_the_variables()
    {
        var stale = _dir.Write("work/overlay/stale.txt", "old");
        var result = AppHostOverlay.Prepare(Request(ProjectAppHost()));

        Assert.False(File.Exists(stale));
        var overlay = _dir.Combine("work", "overlay");
        Assert.Equal([Path.Combine(overlay, "AppHost.cs"), Path.Combine(overlay, "AspireDeployOverrides.g.cs"), Path.Combine(overlay, "ads.overlay.targets")],
            result.Files);
        Assert.Contains("AspireDeployOverrides.Apply(builder);", File.ReadAllText(result.Files[0]));
        Assert.Equal("src/Shop.AppHost/Shop.AppHost.csproj", result.AppHostPath);
        Assert.Null(result.SiblingFile);

        var env = result.Environment;
        Assert.Equal(Path.Combine(overlay, "ads.overlay.targets"), env["CustomAfterMicrosoftCommonTargets"]);
        Assert.Equal("cae-shared", env["Parameters__adsExistingEnvironmentName"]);
        Assert.Equal("rg-shared", env["Parameters__adsExistingEnvironmentResourceGroup"]);
        Assert.Equal("crshared01", env["Parameters__adsExistingRegistryName"]);
        Assert.Equal("rg-shared", env["Parameters__adsExistingRegistryResourceGroup"]);
        Assert.Equal("run-1", env["Parameters__adsOverlayRunId"]);
        Assert.Equal(AppHostOverlay.MarkerFile(_dir.Combine("work"), "run-1"), env["Parameters__adsOverlayMarkerFile"]);
        Assert.False(env.ContainsKey("Parameters__adsExistingPullIdentityName"));

        var targets = XDocument.Load(result.Files[2]);
        Assert.Contains(Path.GetFullPath(_dir.Combine("src", "Shop.AppHost", "Shop.AppHost.csproj")),
            targets.Root!.Element("ItemGroup")!.Attribute("Condition")!.Value);
    }

    [Fact]
    public void Prepare_without_a_registry_sets_no_registry_variables()
    {
        var env = AppHostOverlay.Prepare(Request(ProjectAppHost(), registry: null)).Environment;
        Assert.False(env.ContainsKey("Parameters__adsExistingRegistryName"));
        Assert.False(env.ContainsKey("Parameters__adsExistingRegistryResourceGroup"));
    }

    [Fact]
    public void Prepare_never_changes_the_user_files()
    {
        var info = ProjectAppHost();
        var before = File.ReadAllText(_dir.Combine("src", "Shop.AppHost", "AppHost.cs"));
        AppHostOverlay.Prepare(Request(info));
        Assert.Equal(before, File.ReadAllText(_dir.Combine("src", "Shop.AppHost", "AppHost.cs")));
        Assert.Equal(2, Directory.GetFiles(_dir.Combine("src", "Shop.AppHost")).Length);
    }

    private void Refused(OverlayRequest request, string text)
    {
        var ex = Assert.Throws<DeployerException>(() => AppHostOverlay.Prepare(request));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Contains(text, ex.Message);
        Assert.False(Directory.Exists(_dir.Combine("work", "overlay", "x")));
    }

    [Fact]
    public void Prepare_refuses_locked_sections()
    {
        var info = ProjectAppHost();
        Refused(Request(info with { EnvironmentLocked = true }), "already chooses its own environment");
        Refused(Request(info with { RegistryLocked = true }, env: null), "already chooses its own registry");
        Refused(Request(info with { IdentityLocked = true }, identity: "id-pull"), "already sets its own ACR pull identity");
        // A lock only matters for the section that is overridden.
        AppHostOverlay.Prepare(Request(info with { EnvironmentLocked = true }, env: null));
    }

    [Fact]
    public void Prepare_refuses_a_registry_resource_name_clash() =>
        Refused(Request(ProjectAppHost(), resources: ["ads-existing-registry"]), "already has a resource named ads-existing-registry");

    [Fact]
    public void Prepare_refuses_several_environments_unless_one_is_named()
    {
        var info = ProjectAppHost(environments: ["a", "b"]);
        Refused(Request(info), "declares several Container Apps environments");
        Refused(Request(info, appHostResource: "c"), "no Container Apps environment named c");
        AppHostOverlay.Prepare(Request(info, appHostResource: "b"));
    }

    [Fact]
    public void Prepare_refuses_when_the_builder_file_is_not_found_once()
    {
        var info = ProjectAppHost("var x = 1;");
        Refused(Request(info), "Could not find the file that creates the AppHost builder.");

        _dir.Write("src/Shop.AppHost/AppHost.cs", Fixture("two-builds.cs"));
        Refused(Request(info), "Could not find where the AppHost calls Build().");
    }

    [Fact]
    public void Prepare_skips_bin_obj_and_sibling_files_when_looking_for_the_builder()
    {
        var info = ProjectAppHost();
        _dir.Write("src/Shop.AppHost/obj/Debug/AppHost.cs", AppHostFixtures.AcaAppHostCs);
        _dir.Write("src/Shop.AppHost/.ads-deploy/AppHost.cs", AppHostFixtures.AcaAppHostCs);
        AppHostOverlay.Prepare(Request(info));
    }

    [Theory]
    [InlineData("src/Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("src/Shop.AppHost/Shop.AppHost.csproj")]
    public void Prepare_refuses_a_CustomAfterMicrosoftCommonTargets_property(string file)
    {
        var info = ProjectAppHost();
        var path = _dir.Combine(file.Split('/'));
        var text = File.Exists(path) ? File.ReadAllText(path) : "<Project></Project>";
        File.WriteAllText(path, text.Replace("</Project>", "<PropertyGroup><CustomAfterMicrosoftCommonTargets>x</CustomAfterMicrosoftCommonTargets></PropertyGroup></Project>"));
        Refused(Request(info), "sets CustomAfterMicrosoftCommonTargets");
    }

    [Fact]
    public void Prepare_file_based_writes_a_marked_sibling_with_the_class_appended()
    {
        _dir.Write("apphost.cs", Fixture("filebased.cs"));
        var result = AppHostOverlay.Prepare(Request(Info("apphost.cs", AppHostKind.FileBasedCs, "env"), identity: "id-pull"));

        Assert.Equal(".ads-deploy/apphost.cs", result.AppHostPath);
        Assert.Equal(_dir.Combine(".ads-deploy", "apphost.cs"), result.SiblingFile);
        Assert.False(result.Environment.ContainsKey("CustomAfterMicrosoftCommonTargets"));
        Assert.Equal("id-pull", result.Environment["Parameters__adsExistingPullIdentityName"]);
        var text = File.ReadAllText(result.SiblingFile!).Replace("\r\n", "\n");
        var lines = text.Split('\n');
        Assert.Equal(SiblingOverlay.Marker, lines[0]);
        Assert.StartsWith("#:sdk", lines[1]);
        Assert.True(text.IndexOf("#line default", StringComparison.Ordinal) < text.IndexOf("internal static class AspireDeployOverrides", StringComparison.Ordinal));
        Assert.True(text.IndexOf("builder.Build().Run();", StringComparison.Ordinal) < text.IndexOf("#line default", StringComparison.Ordinal));
        Assert.Equal(Fixture("filebased.cs"), File.ReadAllText(_dir.Combine("apphost.cs")));
    }

    [Fact]
    public void Prepare_typescript_writes_a_marked_sibling()
    {
        _dir.Write("apphost.mts", Fixture("apphost.mts"));
        var result = AppHostOverlay.Prepare(Request(Info("apphost.mts", AppHostKind.TypeScript, "env")));
        Assert.Equal(".ads-deploy/apphost.mts", result.AppHostPath);
        var text = File.ReadAllText(result.SiblingFile!);
        Assert.StartsWith(SiblingOverlay.Marker + "\n", text);
        Assert.Contains("env.publishAsExisting", text);
    }

    [Fact]
    public void Prepare_typescript_without_an_environment_statement_is_refused()
    {
        _dir.Write("apphost.mts", "const builder = await createBuilder();\nawait builder.build().run();\n");
        Refused(Request(Info("apphost.mts", AppHostKind.TypeScript)), "Could not find `const <name> = await builder.addAzureContainerAppEnvironment(...)`");
    }

    [Fact]
    public void Prepare_refuses_a_user_folder_at_the_sibling_path_and_replaces_a_leftover()
    {
        _dir.Write("apphost.cs", Fixture("filebased.cs"));
        var info = Info("apphost.cs", AppHostKind.FileBasedCs, "env");
        var mine = _dir.Write(".ads-deploy/apphost.cs", "// mine\n");
        Refused(Request(info), "A folder named .ads-deploy already exists next to the AppHost and was not written by Aspire Deploy");
        Assert.Equal("// mine\n", File.ReadAllText(mine));

        File.WriteAllText(mine, SiblingOverlay.Marker + "\nold");
        var stale = _dir.Write(".ads-deploy/stale.txt", "x");
        AppHostOverlay.Prepare(Request(info));
        Assert.DoesNotContain("old", File.ReadAllText(mine));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void PatchTypeScript_points_createBuilder_at_the_original_folder()
    {
        var req = Request(Info("apphost.mts", AppHostKind.TypeScript, "env"));
        var patched = AppHostOverlay.PatchTypeScript(Fixture("apphost.mts"), req)!;
        Assert.Contains("createBuilder({ projectDirectory: " + AppHostOverlay.JsString(_dir.Path) + " })", patched);

        var withOptions = Fixture("apphost.mts").Replace("createBuilder()", "createBuilder({ args: [] })");
        Assert.Contains("createBuilder({ projectDirectory: " + AppHostOverlay.JsString(_dir.Path) + ", args: [] })",
            AppHostOverlay.PatchTypeScript(withOptions, req)!);

        Assert.Null(AppHostOverlay.PatchTypeScript(Fixture("apphost.mts").Replace("createBuilder()", "createBuilder(opts)"), req));
        Assert.Null(AppHostOverlay.PatchTypeScript(Fixture("apphost.mts").Replace("const builder = await createBuilder();", "const builder = await make();"), req));
    }

    [Fact]
    public void JsString_escapes_backslashes_quotes_and_control_characters() =>
        Assert.Equal(@"""C:\\a\""b\n\u0001""", AppHostOverlay.JsString(@"C:\a""b" + "\n\u0001"));

    [Fact]
    public void Relative_project_directives_become_absolute()
    {
        var text = AppHostOverlay.AbsoluteProjectDirectives(
            "#:sdk Aspire.AppHost.Sdk@13.6.1\n#:project ./Api/Api.csproj\n#:project " + @"C:\abs\B.csproj" + "\nvar x = 1;\n", @"C:\src");
        Assert.Contains(@"#:project C:\src\Api\Api.csproj", text);
        Assert.Contains(@"#:project C:\abs\B.csproj", text);
        Assert.StartsWith("#:sdk Aspire.AppHost.Sdk@13.6.1\n", text);
    }

    [Fact]
    public void Prepare_file_based_copy_has_absolute_project_paths()
    {
        _dir.Write("apphost.cs", Fixture("filebased.cs"));
        var result = AppHostOverlay.Prepare(Request(Info("apphost.cs", AppHostKind.FileBasedCs, "env")));
        Assert.Contains("#:project " + Path.GetFullPath(_dir.Combine("Api", "Api.csproj")), File.ReadAllText(result.SiblingFile!));
    }
}
