using AspireDeployService.Core.Discovery;
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

    /// <summary>
    /// Applies <paramref name="change"/> to a fresh copy of the profile under the settings lock (D16), then validates.
    /// Used by the edit page so only the fields the user changed are written. Code 2 when the result is invalid.
    /// </summary>
    Task UpdateAsync(string name, Action<DeployProfile> change, CancellationToken ct);

    /// <summary>Removes a profile under its work key lock (code 2 while a run uses it). Optionally deletes its work folder.</summary>
    Task DeleteAsync(string name, bool deleteWorkFolder, CancellationToken ct);

    /// <summary>Sets or clears <c>AspireDeploy:GitHub:Token</c>.</summary>
    Task SetGlobalGitHubTokenAsync(string? token, CancellationToken ct);

    /// <summary>D18: records the AppHost SDK version of a successful deploy.</summary>
    Task RecordDeployedVersionAsync(string name, string aspireVersion, CancellationToken ct);

    /// <summary>Plan T12: a new profile prefilled from discovery, with the parameters to ask for and any warnings.</summary>
    Task<ProfileDraft> DraftFromDiscoveryAsync(string name, SourceSpec source, DiscoveryResult discovery, CancellationToken ct);
}

/// <param name="ToAsk">Parameters with no default and no generator: the wizard asks for each.</param>
/// <param name="Optional">Parameters with a default or a generator: shown read-only.</param>
public sealed record ProfileDraft(string Name, DeployProfile Profile,
    IReadOnlyList<DiscoveredParameter> ToAsk, IReadOnlyList<DiscoveredParameter> Optional, IReadOnlyList<string> Warnings);

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

    public Task UpdateAsync(string name, Action<DeployProfile> change, CancellationToken ct) =>
        store.UpdateAsync(doc =>
        {
            var profile = Require(doc, name);
            var edited = profile.Clone();
            change(edited);
            var errors = ProfileRules.Validate(edited);
            if (errors.Count > 0)
                throw new DeployerException(OutcomeCode.InvalidInput, "Nothing was saved:\n" + string.Join('\n', errors));
            doc.AspireDeploy.Profiles[name] = edited;
        }, ct);

    public Task SetAsync(string name, string key, string? value, bool secret, CancellationToken ct) =>
        store.UpdateAsync(doc => ProfileRules.ApplySet(Require(doc, name), key, value, secret), ct);

    public async Task DeleteAsync(string name, bool deleteWorkFolder, CancellationToken ct)
    {
        var snapshot = await store.LoadAsync(ct);
        if (!snapshot.Document.AspireDeploy.Profiles.ContainsKey(name))
            throw new DeployerException(OutcomeCode.InvalidInput, $"Profile '{name}' does not exist.");

        var workKey = WorkKeys.For(snapshot.Options.WorkRoot, name, null);
        // The folder is deleted under the same lock, so a deploy in another window cannot start in between.
        await using (WorkKeyLock.Acquire(workKey, name))
        {
            await store.UpdateAsync(doc =>
            {
                if (!doc.AspireDeploy.Profiles.Remove(name))
                    throw new DeployerException(OutcomeCode.InvalidInput, $"Profile '{name}' does not exist.");
            }, ct);

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
    }

    public Task SetGlobalGitHubTokenAsync(string? token, CancellationToken ct) =>
        store.UpdateAsync(doc =>
        {
            doc.AspireDeploy.GitHub ??= new GitHubSection();
            doc.AspireDeploy.GitHub.Token = string.IsNullOrEmpty(token) ? null : token;
        }, ct);

    public Task RecordDeployedVersionAsync(string name, string aspireVersion, CancellationToken ct) =>
        store.UpdateAsync(doc => Require(doc, name).LastDeployedAspireVersion = aspireVersion, ct);

    public async Task<ProfileDraft> DraftFromDiscoveryAsync(string name, SourceSpec source, DiscoveryResult discovery, CancellationToken ct)
    {
        ProfileRules.EnsureValidName(name);
        var profile = new DeployProfile
        {
            Source = new ProfileSource
            {
                Kind = source.Kind,
                Location = source.Location,
                Ref = source.Ref,
                AppHostPath = discovery.AppHost.AppHostPath
            },
            Destination = new ProfileDestination { ResourceGroup = $"rg-{name}" },
            Environment = "Production",
            SecretParameters = discovery.Parameters.Where(p => p.Secret).Select(p => p.Name).ToList()
        };

        var warnings = new List<string>();
        if (source.Kind == SourceKind.LocalFolder)
        {
            var snapshot = await store.LoadAsync(ct);
            foreach (var (otherName, other) in snapshot.Document.AspireDeploy.Profiles)
            {
                if (otherName != name && SharesDeployCache(profile, other))
                    warnings.Add($"Profile '{otherName}' deploys the same AppHost with environment {other.EnvironmentName}; "
                        + "the two share Aspire's deploy cache. Use a different Environment name.");
            }
        }

        return new ProfileDraft(name, profile,
            discovery.Parameters.Where(p => p.NeedsValue).ToList(),
            discovery.Parameters.Where(p => !p.NeedsValue).ToList(),
            warnings);
    }

    /// <summary>The shared-cache warning for a profile being edited (its Environment may have changed).</summary>
    public static bool SharesDeployCache(DeployProfile a, DeployProfile b) =>
        a.Source.Kind == SourceKind.LocalFolder && b.Source.Kind == SourceKind.LocalFolder
        && string.Equals(Path.GetFullPath(a.Source.Location).TrimEnd('\\', '/'), Path.GetFullPath(b.Source.Location).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Source.AppHostPath, b.Source.AppHostPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.EnvironmentName, b.EnvironmentName, StringComparison.OrdinalIgnoreCase);

    private static DeployProfile Require(SettingsDocument doc, string name) =>
        doc.AspireDeploy.Profiles.TryGetValue(name, out var p)
            ? p
            : throw new DeployerException(OutcomeCode.InvalidInput, $"Profile '{name}' does not exist.");
}
