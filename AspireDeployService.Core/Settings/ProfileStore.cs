using System.Text.Json;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Outcomes;

namespace AspireDeployService.Core.Settings;

/// <summary>A read of the settings file. <see cref="Document"/> is empty (not null) when the file does not exist yet.</summary>
public sealed record SettingsSnapshot(SettingsLocation Location, SettingsDocument Document)
{
    public AspireDeployOptions Options => AspireDeployOptions.From(Document.AspireDeploy);
}

/// <summary>
/// Reads and writes <c>appsettings.Local.json</c> under implementation plan D16: every change is a locked
/// read-modify-write of the current file; a save goes to a temp file in the same folder and is moved over the
/// original; a file that cannot be parsed is never overwritten.
/// </summary>
public sealed class ProfileStore(ISettingsLocator locator)
{
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    public SettingsLocation Location => Probe(locator.Resolve());

    public async Task<SettingsSnapshot> LoadAsync(CancellationToken ct)
    {
        var location = locator.Resolve();
        var (doc, error) = await ReadAsync(location.Path, ct);
        return new SettingsSnapshot(location with { ParseError = error }, doc ?? new SettingsDocument());
    }

    /// <summary>Applies <paramref name="change"/> to the current file contents and saves the result.</summary>
    public async Task<T> UpdateAsync<T>(Func<SettingsDocument, T> change, CancellationToken ct)
    {
        var location = locator.Resolve();
        FileLock fileLock;
        try
        {
            fileLock = await FileLock.AcquireAsync(location.Path + ".lock", LockTimeout, ct);
        }
        catch (IOException ex)
        {
            throw new DeployerException(OutcomeCode.InvalidInput,
                $"The settings file is busy (another window may be saving it). Try again. ({location.Path})", inner: ex);
        }

        await using (fileLock)
        {
            var (doc, error) = await ReadAsync(location.Path, ct);
            if (error is not null)
                throw new DeployerException(OutcomeCode.InvalidInput,
                    $"The settings file cannot be read, so nothing was saved. Fix it by hand first: {location.Path}: {error}");

            doc ??= new SettingsDocument();
            var result = change(doc);
            await SaveAsync(location.Path, doc, ct);
            return result;
        }
    }

    public Task UpdateAsync(Action<SettingsDocument> change, CancellationToken ct) =>
        UpdateAsync<bool>(d => { change(d); return true; }, ct);

    private static SettingsLocation Probe(SettingsLocation location)
    {
        if (!location.Exists)
            return location;
        try
        {
            using var stream = File.OpenRead(location.Path);
            JsonSerializer.Deserialize<SettingsDocument>(stream, SettingsJson.Options);
            return location;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return location with { ParseError = ex.Message };
        }
    }

    private static async Task<(SettingsDocument? Doc, string? Error)> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return (null, null);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0)
                return (new SettingsDocument(), null);
            var doc = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream, SettingsJson.Options, ct);
            return (doc ?? new SettingsDocument(), null);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
    }

    private static async Task SaveAsync(string path, SettingsDocument doc, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, doc, SettingsJson.Options, ct);
            await stream.FlushAsync(ct);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
