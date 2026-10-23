namespace AspireDeployService.Core.Deploy;

/// <summary>
/// Resource pickers plan D3, as revised by T0: file-based C# and TypeScript AppHosts are deployed from a temporary
/// patched copy in a sibling folder, <c>&lt;dir&gt;/.ads-deploy/&lt;same file name&gt;</c>. T0 showed that the Aspire CLI
/// only recognizes a single-file AppHost named <c>apphost.cs</c> / <c>apphost.ts</c> / <c>apphost.mts</c>, so the copy keeps
/// the name and changes the folder (R12 fallback, Q6). The copy's first line is <see cref="Marker"/>, so a leftover after
/// a crash can be told apart from user files and removed.
/// </summary>
public static class SiblingOverlay
{
    public const string Marker = "// ads-deploy-temp: written by Aspire Deploy during a deploy; safe to delete.";
    public const string FolderName = ".ads-deploy";

    private static readonly string[] Extensions = [".cs", ".ts", ".mts"];

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", ".vs", "aspire-output" };

    /// <summary>"&lt;dir&gt;/.ads-deploy/&lt;name&gt;.&lt;ext&gt;" for "&lt;dir&gt;/&lt;name&gt;.&lt;ext&gt;".</summary>
    public static string SiblingPath(string appHostFullPath) =>
        Path.Combine(Path.GetDirectoryName(appHostFullPath)!, FolderName, Path.GetFileName(appHostFullPath));

    /// <summary>True when the file's first line is exactly <see cref="Marker"/>.</summary>
    public static bool HasMarker(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return reader.ReadLine() is { } first && first.TrimStart('﻿').TrimEnd() == Marker;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// A <c>.ads-deploy</c> folder is ours when it holds an AppHost copy that starts with the marker. Anything else in
    /// it was written by the Aspire CLI for that copy (generated TypeScript modules, a tsconfig).
    /// </summary>
    public static bool IsOurs(string siblingFolder) =>
        Directory.Exists(siblingFolder)
        && Directory.EnumerateFiles(siblingFolder).Any(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && HasMarker(f));

    /// <summary>
    /// Deletes every <c>.ads-deploy</c> folder under <paramref name="root"/> that <see cref="IsOurs"/>. Called at the start
    /// of each deploy and each scan, and when the app starts. Returns the deleted folders for the log.
    /// </summary>
    public static IReadOnlyList<string> CleanupLeftovers(string root)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(root))
            return deleted;
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder);
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || SkippedFolders.Contains(name))
                    continue;
                if (!name.Equals(FolderName, StringComparison.OrdinalIgnoreCase))
                {
                    stack.Push(folder);
                    continue;
                }
                if (!IsOurs(folder))
                    continue;
                try
                {
                    Directory.Delete(folder, recursive: true);
                    deleted.Add(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked: the next run or the next app start removes it.
                }
            }
        }
        return deleted;
    }

    /// <summary>Deletes one sibling folder after a deploy, but only when it is ours. True when it is gone.</summary>
    public static bool Remove(string siblingFile)
    {
        var folder = Path.GetDirectoryName(siblingFile)!;
        if (!Directory.Exists(folder))
            return true;
        if (!IsOurs(folder))
            return false;
        Directory.Delete(folder, recursive: true);
        return true;
    }
}
