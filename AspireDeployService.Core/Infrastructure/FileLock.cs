// Copied from BlazorDataDeploymentWebsite.DeployWorker/Tools/ToolInstaller.cs (implementation plan D2, D17).
// Changed: the retry delay is 250 ms instead of 2 s, because settings saves wait on it interactively.
namespace AspireDeployService.Core.Infrastructure;

/// <summary>An exclusive lock file that waits up to a timeout. Used for the settings file and tool installs (D17).</summary>
public sealed class FileLock : IAsyncDisposable
{
    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    public static async Task<FileLock> AcquireAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
