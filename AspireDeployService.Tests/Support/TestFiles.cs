using System.IO.Compression;

namespace AspireDeployService.Tests.Support;

/// <summary>A temporary folder deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ads-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes a file (creating folders) and returns its full path.</summary>
    public string Write(string relativePath, string content)
    {
        var full = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A killed child process may still hold a handle for a moment; temp files are harmless.
        }
    }
}

public static class Zipball
{
    /// <summary>
    /// Builds an in-memory zip. Copied from BlazorDataDeploymentWebsite.Tests/Worker/ReleaseDownloaderTests.cs
    /// (implementation plan D2).
    /// </summary>
    public static byte[] Create(params (string Path, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }
}
