using System.IO.Compression;
using System.Security.Cryptography;

// Copied from BlazorDataDeploymentWebsite (implementation plan D2): DeployWorker/Releases/SafeZip.cs.
namespace AspireDeployService.Core.Archives;

/// <summary>Zip helpers that treat archives as untrusted input.</summary>
public static class SafeZip
{
    /// <summary>Upper bound on extracted bytes; source archives are small, so this only stops zip bombs.</summary>
    public const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="destination"/>, refusing any entry that
    /// would land outside it (zip slip: <c>../</c>, absolute paths, drive letters).
    /// </summary>
    public static void ExtractToDirectory(string zipPath, string destination)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var target = ResolveEntryPath(rootWithSeparator, entry.FullName);

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            total += entry.Length;
            if (total > MaxExtractedBytes)
                throw new InvalidDataException("The source archive expands beyond the allowed size.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>The absolute path for an entry, or <see cref="InvalidDataException"/> when it escapes the root.</summary>
    public static string ResolveEntryPath(string rootWithSeparator, string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || entryName.Contains('\0'))
            throw new InvalidDataException("The source archive has an entry with an invalid name.");

        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/') || (normalized.Length > 1 && normalized[1] == ':'))
            throw new InvalidDataException($"The source archive entry '{entryName}' has an absolute path.");

        var full = Path.GetFullPath(Path.Combine(rootWithSeparator, normalized));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSeparator, comparison))
            throw new InvalidDataException($"The source archive entry '{entryName}' points outside the extraction folder.");
        return full;
    }

    /// <summary>
    /// GitHub source archives contain exactly one top-level folder (<c>{repo}-{version}/</c> for a tag archive);
    /// that folder is the repository root.
    /// </summary>
    public static string GetSingleTopLevelFolder(string extractedRoot)
    {
        var dirs = Directory.GetDirectories(extractedRoot);
        var files = Directory.GetFiles(extractedRoot);
        if (dirs.Length == 1 && files.Length == 0)
            return dirs[0];
        throw new InvalidDataException("The source archive does not have the single top-level folder of a GitHub source archive.");
    }

    public static async Task<string> Sha256HexAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}
