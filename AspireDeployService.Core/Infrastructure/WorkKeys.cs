using System.Security.Cryptography;
using System.Text;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;

namespace AspireDeployService.Core.Infrastructure;

/// <summary>
/// Where a run keeps its source copy, logs, lock and history (source plan D10, extended in plan §6.2):
/// <c>&lt;WorkRoot&gt;/&lt;profile&gt;</c>; or <c>&lt;WorkRoot&gt;/adhoc-&lt;12 hex&gt;</c> for a source without a profile;
/// or <c>&lt;WorkRoot&gt;/doctor</c> for Doctor without a profile.
/// </summary>
public static class WorkKeys
{
    public static string For(string workRoot, string? profileName, SourceSpec? source)
    {
        if (profileName is not null)
        {
            // Profile names come from a hand-editable file and the folder may be deleted recursively,
            // so "", "..", rooted paths or separators must never become a path (code 2).
            if (!ProfileRules.IsValidName(profileName))
                throw new DeployerException(OutcomeCode.InvalidInput,
                    $"'{profileName}' is not a valid profile name. Rename it in the settings file (2 to 40 lower-case letters, digits or dashes).");
            return Path.Combine(workRoot, profileName);
        }
        if (source is null)
            return Path.Combine(workRoot, "doctor");
        return Path.Combine(workRoot, "adhoc-" + Hash(AdHocIdentity(source)));
    }

    /// <summary>"github:&lt;owner&gt;/&lt;repo&gt;" or "local:&lt;full path, lower-case, no trailing separator&gt;".</summary>
    public static string AdHocIdentity(SourceSpec source) => source.Kind == SourceKind.GitHub
        ? "github:" + source.Location.Trim().ToLowerInvariant()
        : "local:" + Path.GetFullPath(source.Location).TrimEnd('\\', '/').ToLowerInvariant();

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
}

/// <summary>
/// One run per work key (source plan §6.1, plan D17): an exclusive <c>.lock</c> file that fails at once
/// instead of waiting, and is deleted when released.
/// </summary>
public sealed class WorkKeyLock : IAsyncDisposable
{
    private readonly FileStream _stream;

    private WorkKeyLock(FileStream stream) => _stream = stream;

    public static WorkKeyLock Acquire(string workKeyDir, string displayName)
    {
        Directory.CreateDirectory(workKeyDir);
        try
        {
            var stream = new FileStream(Path.Combine(workKeyDir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return new WorkKeyLock(stream);
        }
        catch (IOException ex)
        {
            throw new DeployerException(OutcomeCode.InvalidInput, $"Another run is using {displayName}. Wait for it to finish.", inner: ex);
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
