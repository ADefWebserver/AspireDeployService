using System.Text.Json;
using System.Text.RegularExpressions;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Core.Discovery;

/// <param name="UsesSubmodulesOrLfs">A <c>.gitmodules</c> file or Git LFS pointer files were found.</param>
public sealed record ScanResult(AppHostInfo AppHost, IReadOnlyList<string> Warnings, bool UsesSubmodulesOrLfs);

/// <summary>
/// Discovery stage 1 (source plan §6.4): finds the AppHost, its kind, Aspire version, hosting packages and
/// declared deployment targets by reading files only. It never runs code.
/// </summary>
public static partial class AppHostScanner
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", ".vs", "aspire-output", Deploy.SiblingOverlay.FolderName };

    private const int MaxFilesScanned = 200_000;

    [GeneratedRegex(@"Aspire\.AppHost\.Sdk/(?<v>[0-9][^"";\s]*)")]
    private static partial Regex ProjectSdkAttribute();

    [GeneratedRegex(@"<Sdk\s+Name\s*=\s*""Aspire\.AppHost\.Sdk""\s+Version\s*=\s*""(?<v>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SdkElement();

    [GeneratedRegex(@"<IsAspireHost>\s*true\s*</IsAspireHost>", RegexOptions.IgnoreCase)]
    private static partial Regex IsAspireHost();

    [GeneratedRegex(@"Sdk\s*=\s*""[^""]*\bAspire\.AppHost\.Sdk\b(?!/)[^""]*""")]
    private static partial Regex BareSdk();

    [GeneratedRegex(@"<PackageReference\s+Include\s*=\s*""Aspire\.Hosting\.AppHost""[^>]*?Version\s*=\s*""(?<v>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AppHostPackageVersion();

    [GeneratedRegex(@"<PackageVersion\s+Include\s*=\s*""Aspire\.Hosting\.AppHost""[^>]*?Version\s*=\s*""(?<v>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CentralAppHostVersion();

    [GeneratedRegex(@"<PackageReference\s+Include\s*=\s*""(?<p>Aspire\.Hosting\.[A-Za-z0-9.]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex HostingPackageReference();

    [GeneratedRegex(@"^\s*#:sdk\s+Aspire\.AppHost\.Sdk@(?<v>\S+)", RegexOptions.Multiline)]
    private static partial Regex FileBasedSdk();

    [GeneratedRegex(@"^\s*#:package\s+(?<p>Aspire\.Hosting\.[A-Za-z0-9.]+)@", RegexOptions.Multiline)]
    private static partial Regex FileBasedPackage();

    private static readonly (string[] Signals, DeploymentTarget Target)[] TargetSignals =
    [
        (["Aspire.Hosting.Azure.AppContainers", "AddAzureContainerAppEnvironment", "addAzureContainerAppEnvironment"], DeploymentTarget.AzureContainerApps),
        (["Aspire.Hosting.Azure.AppService", "AddAzureAppServiceEnvironment", "addAzureAppServiceEnvironment"], DeploymentTarget.AzureAppService),
        (["Aspire.Hosting.Azure.Kubernetes", "AddAzureKubernetesEnvironment", "addAzureKubernetesEnvironment"], DeploymentTarget.AzureKubernetes),
        (["AddDockerComposeEnvironment", "addDockerComposeEnvironment"], DeploymentTarget.DockerCompose),
        (["AddKubernetesEnvironment", "addKubernetesEnvironment"], DeploymentTarget.Kubernetes),
    ];

    /// <param name="appHostPath">An explicit AppHost path relative to <paramref name="root"/>, or null to search.</param>
    public static ScanResult Scan(string root, string? appHostPath, AspireDeployOptions options)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
            throw new DeployerException(OutcomeCode.InvalidInput, $"The source folder does not exist: {root}");

        var warnings = new List<string>();
        var walk = Walk(root);

        var (fullPath, kind) = appHostPath is { Length: > 0 }
            ? FromExplicitPath(root, appHostPath)
            : FromConfig(root, walk) ?? FromSearch(root, walk);

        var version = FindVersion(root, fullPath, kind)
            ?? throw new DeployerException(OutcomeCode.InvalidInput,
                $"Could not find the Aspire SDK version of {Relative(root, fullPath)}. Set the AppHost path, or check the project file.");

        if (!AspireVersionRules.IsStrictVersion(version) || !AspireVersionRules.TryParse(version, out var parsed))
            throw new DeployerException(OutcomeCode.InvalidInput, $"'{version}' is not a recognizable Aspire version.");
        if (!AspireVersionRules.IsSupported(parsed, options.MinimumAspireVersion))
            throw new DeployerException(OutcomeCode.UnsupportedVersion, AspireVersionRules.UnsupportedMessage(version));
        if (AspireVersionRules.IsBeyondTested(parsed, options.MaxTestedAspireMajor))
            warnings.Add($"This app is not tested with Aspire {version}; continuing.");

        var packages = FindHostingPackages(root, fullPath, kind);
        var targets = FindTargets(fullPath, kind, packages);
        if (!targets.Any(t => t.IsAzure()))
            warnings.Add("No Azure compute environment is declared. Add one to the AppHost, for example "
                + "builder.AddAzureContainerAppEnvironment(\"env\"); with the Aspire.Hosting.Azure.AppContainers package.");

        var usesSubmodulesOrLfs = false;
        if (File.Exists(Path.Combine(root, ".gitmodules")))
        {
            usesSubmodulesOrLfs = true;
            warnings.Add("This repository uses Git submodules, whose contents are missing from GitHub archives.");
        }
        if (walk.LfsPointer is { } lfs)
        {
            usesSubmodulesOrLfs = true;
            warnings.Add($"This repository uses Git LFS (for example {Relative(root, lfs)}); GitHub archives hold only pointer files.");
        }
        if (walk.Truncated)
            warnings.Add($"The source has more than {MaxFilesScanned:N0} files; the scan stopped early.");

        var source = AppHostSource(fullPath, kind);
        var info = new AppHostInfo(Relative(root, fullPath), kind, version, packages, targets, FindGlobalJsonSdk(root, fullPath))
        {
            EnvironmentLocked = EnvironmentAsExisting().IsMatch(source),
            RegistryLocked = source.Contains("WithAzureContainerRegistry(", StringComparison.Ordinal)
                || source.Contains("AddAzureContainerRegistry(", StringComparison.Ordinal)
                || source.Contains("withAzureContainerRegistry(", StringComparison.Ordinal)
                || source.Contains("addAzureContainerRegistry(", StringComparison.Ordinal),
            IdentityLocked = source.Contains("WithAcrPullIdentity(", StringComparison.Ordinal)
                || source.Contains("withAcrPullIdentity(", StringComparison.Ordinal),
            ContainerAppEnvironmentNames = EnvironmentName().Matches(source).Select(m => m.Groups["n"].Value).Distinct(StringComparer.Ordinal).ToList()
        };
        return new ScanResult(info, warnings, usesSubmodulesOrLfs);
    }

    // Resource pickers plan D4. Single-line mode so a call chain across lines still matches.
    [GeneratedRegex(@"[Aa]ddAzureContainerAppEnvironment\([^;]*?\)\s*\.\s*(Run|Publish|run|publish)?[Aa]sExisting\(", RegexOptions.Singleline)]
    private static partial Regex EnvironmentAsExisting();

    [GeneratedRegex(@"[Aa]ddAzureContainerAppEnvironment\(\s*[""'](?<n>[A-Za-z0-9-]{1,64})[""']")]
    private static partial Regex EnvironmentName();

    /// <summary>Temporary overlay copies (resource pickers plan D3, in a <c>.ads-deploy</c> folder) are never AppHosts.</summary>
    internal static bool IsOverlaySibling(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(Deploy.SiblingOverlay.FolderName, StringComparer.OrdinalIgnoreCase);

    private sealed record WalkResult(List<string> Files, string? LfsPointer, bool Truncated);

    private static WalkResult Walk(string root)
    {
        var files = new List<string>();
        string? lfs = null;
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    var isAspireModules = name.Equals("modules", StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(dir).Equals(".aspire", StringComparison.OrdinalIgnoreCase);
                    if (!SkippedFolders.Contains(name) && !isAspireModules && !name.Equals(".modules", StringComparison.OrdinalIgnoreCase)
                        && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
                        stack.Push(entry);
                    continue;
                }
                if (IsOverlaySibling(entry))
                    continue;
                files.Add(entry);
                if (lfs is null && IsLfsPointer(entry))
                    lfs = entry;
                if (files.Count >= MaxFilesScanned)
                    return new WalkResult(files, lfs, true);
            }
        }
        return new WalkResult(files, lfs, false);
    }

    private static bool IsLfsPointer(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length is 0 or >= 1024)
                return false;
            using var reader = new StreamReader(file);
            var buffer = new char[24];
            var read = reader.Read(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).StartsWith("version https://git-lfs", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (string Path, AppHostKind Kind) FromExplicitPath(string root, string appHostPath)
    {
        var full = Path.GetFullPath(Path.Combine(root, appHostPath));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new DeployerException(OutcomeCode.InvalidInput, $"The AppHost '{appHostPath}' was not found under the source folder.");
        return (full, KindFromFileName(full));
    }

    private static AppHostKind KindFromFileName(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csproj" => AppHostKind.ProjectCsproj,
        ".cs" => AppHostKind.FileBasedCs,
        ".ts" or ".mts" => AppHostKind.TypeScript,
        _ => throw new DeployerException(OutcomeCode.InvalidInput, $"'{Path.GetFileName(path)}' is not an AppHost (.csproj, .cs, .ts or .mts).")
    };

    /// <summary>Row 2: the shallowest aspire.config.json or .aspire/settings.json that names an AppHost stops the search.</summary>
    private static (string, AppHostKind)? FromConfig(string root, WalkResult walk)
    {
        var configs = walk.Files
            .Where(f => Path.GetFileName(f).Equals("aspire.config.json", StringComparison.OrdinalIgnoreCase)
                || (Path.GetFileName(f).Equals("settings.json", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(Path.GetDirectoryName(f))!.Equals(".aspire", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Count(c => c is '\\' or '/'))
            .ThenBy(f => Path.GetFileName(f).Equals("aspire.config.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

        foreach (var config in configs)
        {
            // 13.2+ uses appHost.path; the legacy .aspire/settings.json uses a top-level appHostPath.
            var appHost = ReadJsonString(config, "appHost", "path") ?? ReadTopLevelString(config, "appHostPath");
            if (appHost is null)
                continue;
            var baseDir = Path.GetDirectoryName(config)!;
            if (Path.GetFileName(baseDir).Equals(".aspire", StringComparison.OrdinalIgnoreCase))
                baseDir = Path.GetDirectoryName(baseDir)!; // legacy settings paths are relative to the .aspire folder's parent
            var full = Path.GetFullPath(Path.Combine(baseDir, appHost));
            if (!File.Exists(full) && File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(config)!, appHost))))
                full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(config)!, appHost));
            if (File.Exists(full) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return (full, KindFromFileName(full));
        }
        return null;
    }

    /// <summary>Rows 3 to 6: every file that looks like an AppHost. Exactly one must match.</summary>
    private static (string, AppHostKind) FromSearch(string root, WalkResult walk)
    {
        var found = new List<(string Path, AppHostKind Kind)>();
        foreach (var file in walk.Files)
        {
            var name = Path.GetFileName(file);
            if (name.Equals("apphost.cs", StringComparison.OrdinalIgnoreCase) && FileBasedSdk().IsMatch(Head(file, 4096)))
                found.Add((file, AppHostKind.FileBasedCs));
            else if ((name.Equals("apphost.mts", StringComparison.OrdinalIgnoreCase) || name.Equals("apphost.ts", StringComparison.OrdinalIgnoreCase))
                     && File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "package.json")))
                found.Add((file, AppHostKind.TypeScript));
            else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && LooksLikeAppHostProject(File.ReadAllText(file)))
                found.Add((file, AppHostKind.ProjectCsproj));
        }

        // A folder with both apphost.mts and apphost.ts is one TypeScript AppHost; prefer .mts.
        found = found
            .GroupBy(f => f.Kind == AppHostKind.TypeScript ? Path.GetDirectoryName(f.Path)! : f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => f.Path.EndsWith(".mts", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .ToList();

        return found.Count switch
        {
            1 => found[0],
            0 => throw new DeployerException(OutcomeCode.InvalidInput, "No Aspire AppHost was found in the source folder."),
            _ => throw new DeployerException(OutcomeCode.InvalidInput,
                "More than one AppHost was found. Set the AppHost path to one of:\n"
                + string.Join('\n', found.Select(f => "  " + Relative(root, f.Path))))
        };
    }

    private static bool LooksLikeAppHostProject(string csproj) =>
        ProjectSdkAttribute().IsMatch(csproj) || SdkElement().IsMatch(csproj) || IsAspireHost().IsMatch(csproj) || BareSdk().IsMatch(csproj);

    private static string? FindVersion(string root, string appHost, AppHostKind kind)
    {
        switch (kind)
        {
            case AppHostKind.FileBasedCs:
                return FileBasedSdk().Match(Head(appHost, 4096)) is { Success: true } m ? m.Groups["v"].Value : null;

            case AppHostKind.TypeScript:
                foreach (var dir in Ancestors(Path.GetDirectoryName(appHost)!, root))
                {
                    if (ReadJsonString(Path.Combine(dir, "aspire.config.json"), "sdk", "version") is { } v)
                        return v;
                    if (ReadJsonString(Path.Combine(dir, ".aspire", "settings.json"), "sdk", "version") is { } legacy)
                        return legacy;
                }
                return null;

            default:
                var csproj = File.ReadAllText(appHost);
                if (ProjectSdkAttribute().Match(csproj) is { Success: true } a)
                    return a.Groups["v"].Value;
                if (SdkElement().Match(csproj) is { Success: true } e)
                    return e.Groups["v"].Value;
                if (AppHostPackageVersion().Match(csproj) is { Success: true } p)
                    return p.Groups["v"].Value;
                foreach (var dir in Ancestors(Path.GetDirectoryName(appHost)!, root))
                {
                    if (ReadJsonString(Path.Combine(dir, "global.json"), "msbuild-sdks", "Aspire.AppHost.Sdk") is { } fromGlobal)
                        return fromGlobal;
                    var props = Path.Combine(dir, "Directory.Packages.props");
                    if (File.Exists(props) && CentralAppHostVersion().Match(File.ReadAllText(props)) is { Success: true } c)
                        return c.Groups["v"].Value;
                }
                return null;
        }
    }

    private static IReadOnlyList<string> FindHostingPackages(string root, string appHost, AppHostKind kind)
    {
        var packages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (kind)
        {
            case AppHostKind.ProjectCsproj:
                foreach (Match m in HostingPackageReference().Matches(File.ReadAllText(appHost)))
                    packages.Add(m.Groups["p"].Value);
                break;
            case AppHostKind.FileBasedCs:
                foreach (Match m in FileBasedPackage().Matches(File.ReadAllText(appHost)))
                    packages.Add(m.Groups["p"].Value);
                break;
            case AppHostKind.TypeScript:
                foreach (var dir in Ancestors(Path.GetDirectoryName(appHost)!, root))
                {
                    if (ReadJsonObjectKeys(Path.Combine(dir, "aspire.config.json"), "packages") is { } keys)
                    {
                        foreach (var k in keys.Where(k => k.StartsWith("Aspire.Hosting.", StringComparison.OrdinalIgnoreCase)))
                            packages.Add(k);
                        break;
                    }
                }
                break;
        }
        return packages.ToList();
    }

    private static IReadOnlyList<DeploymentTarget> FindTargets(string appHost, AppHostKind kind, IReadOnlyList<string> packages)
    {
        var text = string.Join('\n', packages) + '\n' + AppHostSource(appHost, kind);
        return TargetSignals
            .Where(t => t.Signals.Any(s => text.Contains(s, StringComparison.Ordinal)))
            .Select(t => t.Target)
            .ToList();
    }

    /// <summary>The AppHost's code: the .cs files of a project (not bin/obj), or the single file.</summary>
    private static string AppHostSource(string appHost, AppHostKind kind) =>
        kind != AppHostKind.ProjectCsproj
            ? File.ReadAllText(appHost)
            : string.Join('\n', ProjectSourceFiles(Path.GetDirectoryName(appHost)!).Select(File.ReadAllText));

    /// <summary>The .cs files of a project folder, not under bin/obj, and not overlay siblings.</summary>
    internal static IEnumerable<string> ProjectSourceFiles(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dir, file).Split('\\', '/');
            if (relative.Any(SkippedFolders.Contains) || IsOverlaySibling(file))
                continue;
            yield return file;
        }
    }

    private static string? FindGlobalJsonSdk(string root, string appHost)
    {
        foreach (var dir in Ancestors(Path.GetDirectoryName(appHost)!, root))
        {
            if (ReadJsonString(Path.Combine(dir, "global.json"), "sdk", "version") is { } v)
                return v;
        }
        return null;
    }

    /// <summary>From <paramref name="start"/> up to and including <paramref name="root"/>.</summary>
    private static IEnumerable<string> Ancestors(string start, string root)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            yield return dir.FullName;
            if (string.Equals(dir.FullName.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                yield break;
        }
    }

    private static string? ReadJsonString(string path, string section, string key)
    {
        var element = ReadJsonSection(path, section);
        return element is { ValueKind: JsonValueKind.Object } obj
               && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadTopLevelString(string path, string key) =>
        ReadJsonSection(path, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static IReadOnlyList<string>? ReadJsonObjectKeys(string path, string section) =>
        ReadJsonSection(path, section) is { ValueKind: JsonValueKind.Object } obj
            ? obj.EnumerateObject().Select(p => p.Name).ToList()
            : null;

    private static JsonElement? ReadJsonSection(string path, string section)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(section, out var value)
                ? value.Clone()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Head(string path, int chars)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[chars];
        var read = reader.ReadBlock(buffer, 0, chars);
        return new string(buffer, 0, read);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
