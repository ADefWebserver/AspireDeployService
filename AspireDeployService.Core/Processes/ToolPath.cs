namespace AspireDeployService.Core.Processes;

/// <summary>
/// The PATH a child process should see. A desktop app captures PATH when it starts, so a tool installed
/// afterwards (az, docker, node) would stay invisible until restart; the registry values are current.
/// </summary>
public static class ToolPath
{
    public static string Current()
    {
        if (!OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable("PATH") ?? "";

        var parts = new[]
        {
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH")
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = parts
            .Where(p => !string.IsNullOrEmpty(p))
            .SelectMany(p => p!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(seen.Add);
        return string.Join(Path.PathSeparator, dirs);
    }

    /// <summary>Puts <paramref name="first"/> folders ahead of the current PATH.</summary>
    public static string With(params string?[] first) =>
        string.Join(Path.PathSeparator, first.Where(f => !string.IsNullOrEmpty(f)).Append(Current()));
}
