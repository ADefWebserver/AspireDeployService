using System.ComponentModel;
using System.Text.Json;
using AspireDeployService.Core.Discovery;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;

namespace AspireDeployService.Core.Prerequisites;

public enum CheckState { Pass, Warn, Fail }

public sealed record PrerequisiteCheck(string Name, CheckState State, string Detail, string? Fix = null);

/// <summary>The checks of source plan §6.6. Each method returns a result instead of throwing.</summary>
public sealed class PrerequisiteChecker
{
    public async Task<PrerequisiteCheck> DotnetSdkAsync(RunContext run, string? requiredByGlobalJson, CancellationToken ct)
    {
        var (ok, lines) = await TryRunAsync(run, "dotnet", ["--list-sdks"], ct);
        if (!ok)
            return new(".NET SDK", CheckState.Fail, "dotnet was not found.", "Install the .NET 10 SDK from https://dot.net.");

        var sdks = lines.Select(l => l.Split(' ')[0]).Where(v => AspireVersionRules.TryParse(v, out _)).ToList();
        var newest = sdks.Select(v => (Text: v, Version: Parse(v))).OrderByDescending(v => v.Version).FirstOrDefault();
        if (newest.Text is null || newest.Version.Major < 10)
            return new(".NET SDK", CheckState.Fail, $"Found: {(sdks.Count == 0 ? "none" : string.Join(", ", sdks))}.", "Install the .NET 10 SDK or later.");

        if (requiredByGlobalJson is not null && AspireVersionRules.TryParse(requiredByGlobalJson, out var required))
        {
            // Approximates rollForward latestFeature: the same major.minor, at least the pinned version.
            var match = sdks.Select(Parse).Any(v => v.Major == required.Major && v.Minor == required.Minor && v >= required);
            if (!match)
                return new(".NET SDK", CheckState.Fail, $"The source's global.json pins {requiredByGlobalJson}; installed: {string.Join(", ", sdks)}.",
                    $"Install the .NET SDK {requiredByGlobalJson} or a later patch of it.");
        }
        return new(".NET SDK", CheckState.Pass, $"Newest: {newest.Text}.");
    }

    /// <summary>Signed in, and (when given) the subscription is visible to the signed-in account.</summary>
    public async Task<PrerequisiteCheck> AzureSignInAsync(RunContext run, string? subscriptionId, string? tenantId, CancellationToken ct)
    {
        var login = tenantId is null ? "az login" : $"az login --tenant {tenantId}";
        var (ok, lines) = await TryRunAsync(run, "az", ["account", "show", "--output", "json"], ct);
        if (!ok)
            return new("Azure CLI sign-in", CheckState.Fail, "The Azure CLI is not installed or not signed in.", $"Install the Azure CLI, then run: {login}");

        var current = TryJson(lines, d => d.RootElement.TryGetProperty("user", out var u) && u.TryGetProperty("name", out var n) ? n.GetString() : null);
        if (subscriptionId is null)
            return new("Azure CLI sign-in", CheckState.Pass, $"Signed in{(current is null ? "" : " as " + current)}.");

        var (listed, list) = await TryRunAsync(run, "az", ["account", "list", "--output", "json"], ct);
        var visible = listed && TryJson(list, d => d.RootElement.ValueKind == JsonValueKind.Array
            && d.RootElement.EnumerateArray().Any(s => s.TryGetProperty("id", out var id)
                && string.Equals(id.GetString(), subscriptionId, StringComparison.OrdinalIgnoreCase)));
        return visible
            ? new("Azure CLI sign-in", CheckState.Pass, $"Signed in; subscription {subscriptionId} is visible.")
            : new("Azure CLI sign-in", CheckState.Fail, $"Subscription {subscriptionId} is not visible to the signed-in account.", $"Run: {login}");
    }

    /// <param name="runtime">"docker", "podman" or null (then ASPIRE_CONTAINER_RUNTIME, then docker).</param>
    public async Task<PrerequisiteCheck> ContainerRuntimeAsync(RunContext run, string? runtime, bool required, CancellationToken ct)
    {
        runtime ??= Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME") is { Length: > 0 } env ? env : "docker";
        var (ok, _) = await TryRunAsync(run, runtime, ["info"], ct);
        if (ok)
            return new("Container runtime", CheckState.Pass, $"{runtime} is running.");
        return new("Container runtime", required ? CheckState.Fail : CheckState.Warn,
            $"{runtime} is not installed or not running." + (required ? " This AppHost builds container images." : ""),
            $"Start {(runtime == "podman" ? "Podman" : "Docker Desktop")}.");
    }

    /// <summary>Node 20.19+, 22.13+ or 24+, for TypeScript AppHosts.</summary>
    public async Task<PrerequisiteCheck> NodeAsync(RunContext run, bool required, CancellationToken ct)
    {
        var (ok, lines) = await TryRunAsync(run, "node", ["--version"], ct);
        var text = lines.FirstOrDefault()?.Trim().TrimStart('v');
        if (!ok || !AspireVersionRules.TryParse(text, out var v))
            return new("Node.js", required ? CheckState.Fail : CheckState.Warn, "Node.js was not found.", "Install Node.js 22 LTS or later.");
        var supported = v.Major >= 24 || (v.Major == 22 && v.Minor >= 13) || (v.Major == 20 && v.Minor >= 19) || v.Major == 23;
        return supported
            ? new("Node.js", CheckState.Pass, $"v{text}.")
            : new("Node.js", required ? CheckState.Fail : CheckState.Warn, $"v{text} is too old.", "Install Node.js 20.19+, 22.13+ or 24+.");
    }

    public async Task<PrerequisiteCheck> GitHubCliAsync(RunContext run, CancellationToken ct)
    {
        var (ok, _) = await TryRunAsync(run, "gh", ["--version"], ct);
        return ok
            ? new("GitHub CLI", CheckState.Pass, "gh is installed; private repositories can use its token.")
            : new("GitHub CLI", CheckState.Warn, "gh is not installed. Private repositories need a token.",
                "Add AspireDeploy:GitHub:Token on the Settings page, or install the GitHub CLI and run gh auth login.");
    }

    /// <summary>Warns when long paths are off and the longest source path plus the work root is near the limit.</summary>
    public PrerequisiteCheck LongPaths(string? sourceRoot)
    {
        var enabled = LongPathsEnabled();
        if (enabled != false)
            return new("Windows long paths", CheckState.Pass, enabled == true ? "Enabled." : "Not applicable.");
        var longest = sourceRoot is null ? 0 : LongestPath(sourceRoot);
        return longest > 240
            ? new("Windows long paths", CheckState.Warn, $"Long paths are off and the source has a {longest}-character path.",
                "Set a shorter AspireDeploy:WorkRoot, or enable long paths (HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\\LongPathsEnabled = 1).")
            : new("Windows long paths", CheckState.Pass, "Off, but the source's paths are short enough.");
    }

    private static bool? LongPathsEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 0);
            return value is int i && i == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static int LongestPath(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Take(200_000).Select(p => p.Length).DefaultIfEmpty(0).Max();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static Version Parse(string v) => AspireVersionRules.TryParse(v, out var parsed) ? parsed : new Version(0, 0);

    private static async Task<(bool Ok, IReadOnlyList<string> StdOut)> TryRunAsync(RunContext run, string file, string[] args, CancellationToken ct)
    {
        try
        {
            var spec = new ProcessSpec(file, args, Path.GetTempPath())
            {
                CaptureStdOut = true,
                LogStdOut = false,
                Timeout = TimeSpan.FromMinutes(2)
            };
            var result = await run.Runner.RunAsync(spec, run.Log, ct);
            return (result.Succeeded, result.StdOut);
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException)
        {
            return (false, []);
        }
    }

    private static T? TryJson<T>(IReadOnlyList<string> lines, Func<JsonDocument, T> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.Join('\n', lines));
            return read(doc);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
