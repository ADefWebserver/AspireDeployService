using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;

namespace AspireDeployService.Core.Services;

public sealed record ProfileSummary(string Name, SourceKind SourceKind, string SourceDisplay, string? SubscriptionId, string? ResourceGroup);

public interface IProfileService
{
    /// <summary>The resolved settings file; re-resolved on every call. <c>ParseError</c> set means read-only (D16).</summary>
    SettingsLocation Location { get; }

    Task<SettingsSnapshot> LoadAsync(CancellationToken ct);
    Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken ct);
    Task<DeployProfile?> GetAsync(string name, CancellationToken ct);

    /// <summary>Validates and saves a whole profile. Code 2 if it exists and <paramref name="overwrite"/> is false, or if it is invalid.</summary>
    Task SaveAsync(string name, DeployProfile profile, bool overwrite, CancellationToken ct);

    /// <summary>Sets one key (source plan §6.1 settable keys). Code 2 for an unknown key or a missing profile.</summary>
    Task SetAsync(string name, string key, string? value, bool secret, CancellationToken ct);

    /// <summary>Removes a profile under its work key lock (code 2 while a run uses it). Optionally deletes its work folder.</summary>
    Task DeleteAsync(string name, bool deleteWorkFolder, CancellationToken ct);

    /// <summary>Sets or clears <c>AspireDeploy:GitHub:Token</c>.</summary>
    Task SetGlobalGitHubTokenAsync(string? token, CancellationToken ct);

    /// <summary>D18: records the AppHost SDK version of a successful deploy.</summary>
    Task RecordDeployedVersionAsync(string name, string aspireVersion, CancellationToken ct);
}

public sealed class ProfileService(ProfileStore store) : IProfileService
{
    public SettingsLocation Location => store.Location;

    public Task<SettingsSnapshot> LoadAsync(CancellationToken ct) => store.LoadAsync(ct);

    public async Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken ct)
    {
        var snapshot = await store.LoadAsync(ct);
        return snapshot.Document.AspireDeploy.Profiles
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new ProfileSummary(p.Key, p.Value.Source.Kind, p.Value.ToSourceSpec().Display,
                p.Value.Destination.SubscriptionId, p.Value.Destination.ResourceGroup))
            .ToList();
    }

    public async Task<DeployProfile?> GetAsync(string name, CancellationToken ct)
    {
        var snapshot = await store.LoadAsync(ct);
        return snapshot.Document.AspireDeploy.Profiles.TryGetValue(name, out var p) ? p.Clone() : null;
    }

    public Task SaveAsync(string name, DeployProfile profile, bool overwrite, CancellationToken ct)
    {
        ProfileRules.EnsureValidName(name);
        var errors = ProfileRules.Validate(profile);
        if (errors.Count > 0)
            throw new DeployerException(OutcomeCode.InvalidInput, "The profile is not valid:\n" + string.Join('\n', errors));

        return store.UpdateAsync(doc =>
        {
            var profiles = doc.AspireDeploy.Profiles;
            if (profiles.ContainsKey(name) && !overwrite)
                throw new DeployerException(OutcomeCode.InvalidInput, $"Profile '{name}' already exists. Tick Overwrite to replace it.");
            profiles[name] = profile.Clone();
        }, ct);
    }

    public Task SetAsync(string name, string key, string? value, bool secret, CancellationToken ct) =>
        store.UpdateAsync(doc => ProfileRules.ApplySet(Require(doc, name), key, value, secret), ct);

    public async Task DeleteAsync(string name, bool deleteWorkFolder, CancellationToken ct)
    {
        var snapshot = await store.LoadAsync(ct);
        if (!snapshot.Document.AspireDeploy.Profiles.ContainsKey(name))
            throw new DeployerException(OutcomeCode.InvalidInput, $"Profile '{name}' does not exist.");

        var workKey = WorkKeys.For(snapshot.Options.WorkRoot, name, null);
        await using (WorkKeyLock.Acquire(workKey, name))
        {
            await store.UpdateAsync(doc =>
            {
                if (!doc.AspireDeploy.Profiles.Remove(name))
                    throw new DeployerException(OutcomeCode.InvalidInput, $"Profile '{name}' does not exist.");
            }, ct);
        }

        if (deleteWorkFolder && Directory.Exists(workKey))
        {
            try
            {
                Directory.Delete(workKey, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DeployerException(OutcomeCode.InvalidInput,
                    $"The profile was deleted, but its work folder could not be removed: {workKey} ({ex.Message})");
            }
        }
    }

    public Task SetGlobalGitHubTokenAsync(string? token, CancellationToken ct) =>
        store.UpdateAsync(doc =>
        {
            doc.AspireDeploy.GitHub ??= new GitHubSection();
            doc.AspireDeploy.GitHub.Token = string.IsNullOrEmpty(token) ? null : token;
        }, ct);

    public Task RecordDeployedVersionAsync(string name, string aspireVersion, CancellationToken ct) =>
        store.UpdateAsync(doc => Require(doc, name).LastDeployedAspireVersion = aspireVersion, ct);

    private static DeployProfile Require(SettingsDocument doc, string name) =>
        doc.AspireDeploy.Profiles.TryGetValue(name, out var p)
            ? p
            : throw new DeployerException(OutcomeCode.InvalidInput, $"Profile '{name}' does not exist.");
}
