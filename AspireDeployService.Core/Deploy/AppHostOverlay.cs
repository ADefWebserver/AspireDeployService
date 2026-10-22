using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Outcomes;

namespace AspireDeployService.Core.Deploy;

/// <param name="AppHost">The scanned AppHost; its path is relative to <paramref name="SourceRoot"/>.</param>
/// <param name="RunId">Fresh GUID per deploy, echoed by the marker line.</param>
/// <param name="ResourceNames">Manifest resource names, to refuse a clash with ads-existing-registry.</param>
/// <param name="EnvironmentName">null = leave the environment to the AppHost.</param>
/// <param name="RegistryName">null = leave the registry to the AppHost.</param>
/// <param name="PullIdentityName">null = leave the pull identity to Aspire (D13).</param>
public sealed record OverlayRequest(
    string SourceRoot, AppHostInfo AppHost, string WorkKey, string RunId, IReadOnlyCollection<string> ResourceNames,
    string? EnvironmentAppHostResource,
    string? EnvironmentName, string? EnvironmentResourceGroup,
    string? RegistryName, string? RegistryResourceGroup,
    string? PullIdentityName, string? PullIdentityResourceGroup)
{
    public bool EnvironmentLocked => AppHost.EnvironmentLocked;
    public bool RegistryLocked => AppHost.RegistryLocked;
    public bool IdentityLocked => AppHost.IdentityLocked;
}

/// <param name="Environment">Variables to merge into the aspire deploy ProcessSpec.</param>
/// <param name="Files">Absolute paths written, for the log.</param>
/// <param name="AppHostPath">The path to pass to aspire deploy --apphost: the original for a project AppHost,
/// the sibling copy (relative to SourceRoot) for file-based and TypeScript AppHosts.</param>
/// <param name="SiblingFile">Full path of the sibling copy to delete after the deploy; null for a project AppHost.</param>
public sealed record OverlayResult(IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Files,
    string AppHostPath, string? SiblingFile);

/// <summary>
/// Resource pickers plan §6.4 (F2): makes the user's AppHost mark the chosen environment, registry and pull identity as
/// existing, without changing the user's files. A project AppHost gets a patched copy of its entry file swapped in through
/// <c>CustomAfterMicrosoftCommonTargets</c> (D2); a file-based or TypeScript AppHost gets a temporary sibling (D3).
/// Names travel as <c>Parameters__adsExisting*</c> values, never as code (D12).
/// </summary>
public static partial class AppHostOverlay
{
    public const string ParamEnvName = "adsExistingEnvironmentName";
    public const string ParamEnvGroup = "adsExistingEnvironmentResourceGroup";
    public const string ParamRegistryName = "adsExistingRegistryName";
    public const string ParamRegistryGroup = "adsExistingRegistryResourceGroup";
    public const string ParamIdentityName = "adsExistingPullIdentityName";
    public const string ParamIdentityGroup = "adsExistingPullIdentityResourceGroup";
    public const string ParamRunId = "adsOverlayRunId";
    public const string ParamMarkerFile = "adsOverlayMarkerFile";
    public const string MarkerPrefix = "ADS-OVERLAY-APPLIED ";

    /// <summary>
    /// The proof that the overlay ran (§6.4). T0 showed that AppHost stdout does not reach <c>aspire deploy</c>'s output,
    /// so the overlay writes the run ID to this file in the overlay folder instead.
    /// </summary>
    public static string MarkerFile(string workKey, string runId) => Path.Combine(workKey, "overlay", $"applied-{runId}.txt");
    public const string RegistryResourceName = "ads-existing-registry";
    public const string IdentityResourceName = "ads-existing-pull-identity";
    public const string TargetsFileName = "ads.overlay.targets";
    public const string GeneratedFileName = "AspireDeployOverrides.g.cs";

    /// <summary>The Aspire version the generated code was last checked against (R2).</summary>
    public const string TestedAspireVersion = "13.6.2";

    [GeneratedRegex(@"\b(var|IDistributedApplicationBuilder)\s+(?<b>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*DistributedApplication\s*\.\s*CreateBuilder\s*\(")]
    private static partial Regex BuilderDeclaration();

    [GeneratedRegex(@"\bconst\s+(?<v>[A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*await\s+(?<b>[A-Za-z_$][A-Za-z0-9_$]*)\s*\.\s*addAzureContainerAppEnvironment\s*\(")]
    private static partial Regex TsEnvironmentDeclaration();

    [GeneratedRegex(@"\bCustomAfterMicrosoftCommonTargets\b", RegexOptions.IgnoreCase)]
    private static partial Regex CustomAfterProperty();

    public static OverlayResult Prepare(OverlayRequest request)
    {
        var appHostFull = Path.GetFullPath(Path.Combine(request.SourceRoot, request.AppHost.AppHostPath));
        var kind = request.AppHost.Kind;
        string? sibling = null;

        if (kind is AppHostKind.FileBasedCs or AppHostKind.TypeScript)
        {
            sibling = SiblingOverlay.SiblingPath(appHostFull);
            var folder = Path.GetDirectoryName(sibling)!;
            if (Directory.Exists(folder))
            {
                if (!SiblingOverlay.IsOurs(folder))
                    throw Invalid($"A folder named {SiblingOverlay.FolderName} already exists next to the AppHost and was not written by Aspire Deploy. Rename or delete it.");
                Directory.Delete(folder, recursive: true);
            }
        }

        if (request.PullIdentityName is not null && request.IdentityLocked)
            throw Invalid("The AppHost already sets its own ACR pull identity. Set its parameters on the Parameters step instead.");
        if (request.EnvironmentName is not null && request.EnvironmentLocked)
            throw Invalid("The AppHost already chooses its own environment. Set its parameters on the Parameters step instead.");
        if (request.RegistryName is not null && request.RegistryLocked)
            throw Invalid("The AppHost already chooses its own registry. Set its parameters on the Parameters step instead.");
        if (request.RegistryName is not null && request.ResourceNames.Contains(RegistryResourceName, StringComparer.OrdinalIgnoreCase))
            throw Invalid($"The AppHost already has a resource named {RegistryResourceName}.");

        var environments = request.AppHost.ContainerAppEnvironmentNames;
        var target = request.EnvironmentAppHostResource;
        if (target is not null)
        {
            if (!Settings.ProfileRules.AppHostResourcePattern().IsMatch(target) || !environments.Contains(target, StringComparer.Ordinal))
                throw Invalid($"The AppHost has no Container Apps environment named {target}. Choose which one on the profile page.");
        }
        else if (environments.Count > 1)
            throw Invalid("The AppHost declares several Container Apps environments. Choose which one on the profile page.");

        var overlayDir = Path.Combine(request.WorkKey, "overlay");
        if (Directory.Exists(overlayDir))
            Directory.Delete(overlayDir, recursive: true);
        Directory.CreateDirectory(overlayDir);

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Parameters__" + ParamRunId] = request.RunId,
            ["Parameters__" + ParamMarkerFile] = MarkerFile(request.WorkKey, request.RunId)
        };
        if (request.EnvironmentName is not null)
        {
            env["Parameters__" + ParamEnvName] = request.EnvironmentName;
            env["Parameters__" + ParamEnvGroup] = request.EnvironmentResourceGroup!;
        }
        if (request.RegistryName is not null)
        {
            env["Parameters__" + ParamRegistryName] = request.RegistryName;
            env["Parameters__" + ParamRegistryGroup] = request.RegistryResourceGroup!;
        }
        if (request.PullIdentityName is not null)
        {
            env["Parameters__" + ParamIdentityName] = request.PullIdentityName;
            env["Parameters__" + ParamIdentityGroup] = request.PullIdentityResourceGroup!;
        }

        var files = new List<string>();
        switch (kind)
        {
            case AppHostKind.ProjectCsproj:
            {
                var projectDir = Path.GetDirectoryName(appHostFull)!;
                CheckNoCustomAfter(appHostFull);
                var entries = AppHostScanner.ProjectSourceFiles(projectDir)
                    .Where(f => File.ReadAllText(f).Contains("DistributedApplication.CreateBuilder(", StringComparison.Ordinal))
                    .ToList();
                if (entries.Count != 1)
                    throw Invalid("Could not find the file that creates the AppHost builder.");
                var entry = entries[0];
                var patched = Patch(File.ReadAllText(entry), entry) ?? throw NoBuild();

                var patchedPath = Path.Combine(overlayDir, Path.GetFileName(entry));
                var generatedPath = Path.Combine(overlayDir, GeneratedFileName);
                var targetsPath = Path.Combine(overlayDir, TargetsFileName);
                File.WriteAllText(patchedPath, patched, Utf8NoBom);
                File.WriteAllText(generatedPath, GeneratedClass(request), Utf8NoBom);
                File.WriteAllText(targetsPath, Targets(appHostFull, Path.GetRelativePath(projectDir, entry), patchedPath, generatedPath), Utf8NoBom);
                files.AddRange([patchedPath, generatedPath, targetsPath]);
                env["CustomAfterMicrosoftCommonTargets"] = targetsPath;
                return new OverlayResult(env, files, request.AppHost.AppHostPath, null);
            }
            case AppHostKind.FileBasedCs:
            {
                var patched = Patch(AbsoluteProjectDirectives(File.ReadAllText(appHostFull), Path.GetDirectoryName(appHostFull)!), appHostFull)
                    ?? throw NoBuild();
                var text = SiblingOverlay.Marker + "\n" + patched.TrimEnd() + "\n\n#line default\n" + GeneratedClass(request);
                Directory.CreateDirectory(Path.GetDirectoryName(sibling!)!);
                File.WriteAllText(sibling!, text, Utf8NoBom);
                files.Add(sibling!);
                return new OverlayResult(env, files, Relative(request.SourceRoot, sibling!), sibling);
            }
            default:
            {
                var patched = PatchTypeScript(File.ReadAllText(appHostFull), request)
                    ?? throw Invalid("Could not find `const <name> = await builder.addAzureContainerAppEnvironment(...)` in the AppHost. "
                        + "See the guide for the lines to add yourself.");
                Directory.CreateDirectory(Path.GetDirectoryName(sibling!)!);
                File.WriteAllText(sibling!, SiblingOverlay.Marker + "\n" + patched, Utf8NoBom);
                files.Add(sibling!);
                return new OverlayResult(env, files, Relative(request.SourceRoot, sibling!), sibling);
            }
        }
    }

    /// <summary>
    /// Inserts <c>AspireDeployOverrides.Apply(builder);</c> before the line holding <c>builder.Build()</c>, with
    /// <c>#line</c> directives so compiler errors still point at the user's file. Null when the builder variable or its
    /// Build() call cannot be found exactly once.
    /// </summary>
    internal static string? Patch(string source, string originalPath)
    {
        var declarations = BuilderDeclaration().Matches(source);
        if (declarations.Count != 1)
            return null;
        var b = declarations[0].Groups["b"].Value;
        var builds = Regex.Matches(source, @"\b" + Regex.Escape(b) + @"\s*\.\s*Build\s*\(\s*\)");
        if (builds.Count != 1 || builds[0].Index < declarations[0].Index)
            return null;

        var lineStart = source.LastIndexOf('\n', Math.Max(0, builds[0].Index - 1)) + 1;
        var lineNumber = 1 + source.AsSpan(0, lineStart).Count('\n');
        var indent = new string(source[lineStart..].TakeWhile(c => c is ' ' or '\t').ToArray());
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insert = $"#line hidden{newline}{indent}AspireDeployOverrides.Apply({b});{newline}#line {lineNumber} \"{originalPath}\"{newline}";
        return source.Insert(lineStart, insert);
    }

    /// <summary>
    /// Inserts the overlay calls after <c>const env = await builder.addAzureContainerAppEnvironment(...);</c>. Null when
    /// that statement or <c>builder.build()</c> cannot be found exactly once.
    /// </summary>
    internal static string? PatchTypeScript(string source, OverlayRequest request)
    {
        var matches = TsEnvironmentDeclaration().Matches(source).Where(m => request.EnvironmentAppHostResource is not { } name
            || source[(m.Index + m.Length)..].TrimStart().StartsWith($"\"{name}\"", StringComparison.Ordinal)
            || source[(m.Index + m.Length)..].TrimStart().StartsWith($"'{name}'", StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
            return null;
        var m = matches[0];
        var v = m.Groups["v"].Value;
        var b = m.Groups["b"].Value;
        if (Regex.Matches(source, @"\b" + Regex.Escape(b) + @"\s*\.\s*build\s*\(\s*\)").Count != 1)
            return null;

        var end = StatementEnd(source, m.Index + m.Length);
        if (end < 0)
            return null;

        // The CLI runs the copy as its own AppHost folder (T0), so paths such as addNodeApp("api", "./api") must still
        // resolve from the user's folder: createBuilder() gets that folder as its projectDirectory.
        var create = TsCreateBuilder().Matches(source);
        if (create.Count != 1 || create[0].Index > m.Index)
            return null;
        var projectDir = JsString(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(request.SourceRoot, request.AppHost.AppHostPath)))!);
        var c = create[0];
        var createArgs = c.Groups["args"].Value;
        string replacement;
        if (createArgs.Trim().Length == 0)
            replacement = $"createBuilder({{ projectDirectory: {projectDir} }})";
        else if (createArgs.TrimStart().StartsWith('{'))
        {
            var brace = createArgs.IndexOf('{');
            replacement = "createBuilder(" + createArgs[..(brace + 1)] + $" projectDirectory: {projectDir}," + createArgs[(brace + 1)..] + ")";
        }
        else
            return null;
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = new List<string>
        {
            "",
            "// Added by Aspire Deploy for this deploy only.",
            $"(await import(\"node:fs\")).writeFileSync(process.env[\"Parameters__{ParamMarkerFile}\"] ?? \"\", \"{MarkerPrefix}\" + process.env[\"Parameters__{ParamRunId}\"]);"
        };
        if (request.EnvironmentName is not null)
            lines.Add($"await {v}.publishAsExisting(await {b}.addParameter(\"{ParamEnvName}\"), {{ resourceGroup: await {b}.addParameter(\"{ParamEnvGroup}\") }});");
        if (request.RegistryName is not null)
        {
            lines.Add($"const adsRegistry = await {b}.addAzureContainerRegistry(\"{RegistryResourceName}\");");
            lines.Add($"await adsRegistry.publishAsExisting(await {b}.addParameter(\"{ParamRegistryName}\"), {{ resourceGroup: await {b}.addParameter(\"{ParamRegistryGroup}\") }});");
            lines.Add($"await {v}.withAzureContainerRegistry(adsRegistry);");
        }
        if (request.PullIdentityName is not null)
        {
            lines.Add($"const adsPullIdentity = await {b}.addAzureUserAssignedIdentity(\"{IdentityResourceName}\");");
            lines.Add($"await adsPullIdentity.publishAsExisting(await {b}.addParameter(\"{ParamIdentityName}\"), {{ resourceGroup: await {b}.addParameter(\"{ParamIdentityGroup}\") }});");
            lines.Add($"await {v}.withAcrPullIdentity(adsPullIdentity);");
        }
        var inserted = source.Insert(end, newline + string.Join(newline, lines));
        return inserted[..c.Index] + replacement + inserted[(c.Index + c.Length)..];
    }

    /// <summary>A double-quoted JavaScript string literal.</summary>
    internal static string JsString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when ch < (char)0x20 || ch == (char)0x2028 || ch == (char)0x2029 => $"\\u{(int)ch:x4}",
                _ => ch.ToString()
            });
        }
        return sb.Append('"').ToString();
    }

    [GeneratedRegex(@"\bcreateBuilder\s*\((?<args>[^()]*)\)")]
    private static partial Regex TsCreateBuilder();

    [GeneratedRegex(@"^(?<lead>[ \t]*#:project[ \t]+)(?<path>[^\r\n]+?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex ProjectDirective();

    /// <summary>The copy lives one folder down, so relative <c>#:project</c> paths are made absolute.</summary>
    internal static string AbsoluteProjectDirectives(string source, string originalFolder) =>
        ProjectDirective().Replace(source, m =>
        {
            var path = m.Groups["path"].Value.Trim().Trim('"');
            return Path.IsPathRooted(path) ? m.Value : m.Groups["lead"].Value + Path.GetFullPath(Path.Combine(originalFolder, path));
        });

    /// <summary>The index just after the ';' that ends the statement starting at <paramref name="from"/> (parentheses balanced).</summary>
    private static int StatementEnd(string source, int from)
    {
        var depth = 1; // the '(' of addAzureContainerAppEnvironment( is already open
        char? quote = null;
        for (var i = from; i < source.Length; i++)
        {
            var c = source[i];
            if (quote is not null)
            {
                if (c == '\\') i++;
                else if (c == quote) quote = null;
                continue;
            }
            switch (c)
            {
                case '"' or '\'' or '`': quote = c; break;
                case '(': depth++; break;
                case ')': depth--; break;
                case ';' when depth == 0:
                    return i + 1;
                case '\n' when depth == 0 && !NextLineContinues(source, i + 1):
                    return i;
            }
        }
        return -1;
    }

    private static bool NextLineContinues(string source, int from)
    {
        var rest = source.AsSpan(from).TrimStart();
        return rest.Length > 0 && rest[0] == '.';
    }

    /// <summary>The fixed template. Only <see cref="OverlayRequest.EnvironmentAppHostResource"/> (validated) is written as a literal.</summary>
    internal static string GeneratedClass(OverlayRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// <auto-generated/> by Aspire Deploy for one deploy run. Not part of the user's source. Checked with Aspire {TestedAspireVersion}.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("internal static class AspireDeployOverrides");
        sb.AppendLine("{");
        sb.AppendLine("    public static void Apply(global::Aspire.Hosting.IDistributedApplicationBuilder builder)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!builder.ExecutionContext.IsPublishMode)");
        sb.AppendLine("            return;");
        sb.AppendLine($"        if (builder.Configuration[\"Parameters:{ParamMarkerFile}\"] is {{ Length: > 0 }} adsMarker)");
        sb.AppendLine($"            global::System.IO.File.WriteAllText(adsMarker, \"{MarkerPrefix}\" + builder.Configuration[\"Parameters:{ParamRunId}\"]);");
        sb.AppendLine("        var environments = global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.OfType<global::Aspire.Hosting.Azure.AppContainers.AzureContainerAppEnvironmentResource>(builder.Resources));");
        if (request.EnvironmentAppHostResource is { } name)
            sb.AppendLine($"        environments = environments.FindAll(e => e.Name == \"{name}\");");
        sb.AppendLine("        if (environments.Count != 1)");
        sb.AppendLine("            throw new global::System.InvalidOperationException(\"Aspire Deploy: expected one Container Apps environment in the AppHost, found \" + environments.Count + \".\");");
        sb.AppendLine("        var environment = builder.CreateResourceBuilder(environments[0]);");
        if (request.EnvironmentName is not null)
            sb.AppendLine($"        global::Aspire.Hosting.ExistingAzureResourceExtensions.PublishAsExisting(environment, {Param(ParamEnvName)}, {Param(ParamEnvGroup)});");
        if (request.RegistryName is not null)
        {
            sb.AppendLine($"        var registry = global::Aspire.Hosting.AzureContainerRegistryExtensions.AddAzureContainerRegistry(builder, \"{RegistryResourceName}\");");
            sb.AppendLine($"        global::Aspire.Hosting.ExistingAzureResourceExtensions.PublishAsExisting(registry, {Param(ParamRegistryName)}, {Param(ParamRegistryGroup)});");
            sb.AppendLine("        global::Aspire.Hosting.AzureContainerRegistryExtensions.WithAzureContainerRegistry(environment, registry);");
        }
        if (request.PullIdentityName is not null)
        {
            sb.AppendLine($"        var identity = global::Aspire.Hosting.Azure.AzureUserAssignedIdentityExtensions.AddAzureUserAssignedIdentity(builder, \"{IdentityResourceName}\");");
            sb.AppendLine($"        global::Aspire.Hosting.ExistingAzureResourceExtensions.PublishAsExisting(identity, {Param(ParamIdentityName)}, {Param(ParamIdentityGroup)});");
            sb.AppendLine("        global::Aspire.Hosting.AzureContainerAppExtensions.WithAcrPullIdentity(environment, identity);");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();

        static string Param(string name) => $"global::Aspire.Hosting.ParameterResourceBuilderExtensions.AddParameter(builder, \"{name}\")";
    }

    internal static string Targets(string projectFullPath, string entryRelative, string patchedPath, string generatedPath) => $"""
        <Project>
          <!-- Generated by Aspire Deploy for one deploy run. Applies to the AppHost project only. -->
          <ItemGroup Condition="'$(MSBuildProjectFullPath)' == '{Escape(projectFullPath)}'">
            <Compile Remove="{Escape(entryRelative)}" />
            <Compile Include="{Escape(patchedPath)}" />
            <Compile Include="{Escape(generatedPath)}" />
          </ItemGroup>
        </Project>

        """;

    /// <summary>MSBuild-escapes (%XX) the characters MSBuild treats specially, then XML-escapes the result.</summary>
    internal static string Escape(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (var c in path)
        {
            sb.Append(c switch
            {
                '%' => "%25",
                '$' => "%24",
                '@' => "%40",
                ';' => "%3B",
                '\'' => "%27",
                '*' => "%2A",
                '?' => "%3F",
                _ => c.ToString()
            });
        }
        return SecurityElement.Escape(sb.ToString());
    }

    /// <summary>
    /// A property set in the project or a Directory.Build file would win over the environment variable and the overlay
    /// would be silently skipped (R9).
    /// </summary>
    private static void CheckNoCustomAfter(string projectFullPath)
    {
        var candidates = new List<string> { projectFullPath };
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(projectFullPath)!); dir is not null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "Directory.Build.props"));
            candidates.Add(Path.Combine(dir.FullName, "Directory.Build.targets"));
        }
        foreach (var file in candidates.Where(File.Exists))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (CustomAfterProperty().IsMatch(text))
                throw Invalid("The AppHost project (or a Directory.Build file) sets CustomAfterMicrosoftCommonTargets, so the overlay cannot be added. "
                    + $"({file})");
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static DeployerException NoBuild() =>
        Invalid("Could not find where the AppHost calls Build(). See the guide for the lines to add yourself.");

    private static DeployerException Invalid(string message) => new(OutcomeCode.InvalidInput, message);
}
