namespace AspireDeployService.Core.Settings;

/// <summary>
/// The state of the profile edit page (plan §6.1 <c>/profiles/{name}</c>), kept out of Razor so it can be tested.
/// Saved secret values are never put into the form: such a parameter is "masked" and can only be revealed,
/// replaced or cleared. <see cref="ApplyTo"/> writes only what the user changed onto a fresh copy of the
/// profile read under the settings lock (D16), so another window's concurrent changes survive.
/// </summary>
public sealed class ProfileEditSession
{
    private readonly HashSet<string> _secret;
    private readonly Dictionary<string, string> _replacements = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cleared = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

    public ProfileEditSession(DeployProfile original)
    {
        Original = original.Clone();
        Edited = original.Clone();
        foreach (var name in Original.SecretParameters.Where(Edited.Parameters.ContainsKey))
            Edited.Parameters[name] = "";
        Edited.Source.GitHubToken = null;
        _secret = new HashSet<string>(Original.SecretParameters, StringComparer.Ordinal);
    }

    /// <summary>The profile as loaded, including secret values (used only for Reveal).</summary>
    public DeployProfile Original { get; }

    /// <summary>The form's copy. Masked secrets hold "" and the token is null.</summary>
    public DeployProfile Edited { get; }

    /// <summary>A new token typed after "Replace"; empty or null means "keep the current one".</summary>
    public string? TokenReplacement { get; set; }

    public bool TokenCleared { get; set; }

    /// <summary>The single rule for both rendering and saving: the saved value is a secret that was not loaded into the form.</summary>
    public bool IsMasked(string name) =>
        !_removed.Contains(name) && Original.SecretParameters.Contains(name) && Original.Parameters.ContainsKey(name);

    public bool IsSecret(string name) => _secret.Contains(name);

    public void SetSecret(string name, bool secret)
    {
        if (secret) _secret.Add(name); else _secret.Remove(name);
    }

    public string? ReplacementFor(string name) => _replacements.GetValueOrDefault(name);

    /// <summary>For a masked secret: null cancels the replacement; "" opens an empty input (still "keep current").</summary>
    public void Replace(string name, string? value)
    {
        if (value is null) _replacements.Remove(name); else _replacements[name] = value;
        if (value is not null) _cleared.Remove(name);
    }

    public bool IsCleared(string name) => _cleared.Contains(name);

    public void Clear(string name, bool cleared)
    {
        if (cleared) { _cleared.Add(name); _replacements.Remove(name); } else _cleared.Remove(name);
    }

    public void Remove(string name)
    {
        Edited.Parameters.Remove(name);
        Edited.ParameterConfigKeys.Remove(name);
        _replacements.Remove(name);
        _cleared.Remove(name);
        _secret.Remove(name);
        if (Original.Parameters.ContainsKey(name))
            _removed.Add(name);
    }

    /// <summary>Adds a parameter with an empty value. A re-added name is a plain value, no longer masked.</summary>
    public bool Add(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || Edited.Parameters.ContainsKey(name))
            return false;
        Edited.Parameters[name] = "";
        return true;
    }

    public void ApplyTo(DeployProfile p)
    {
        var o = Original;
        var e = Edited;

        Copy(o.Source.Ref, e.Source.Ref, v => p.Source.Ref = v);
        Copy(o.Source.AppHostPath, e.Source.AppHostPath, v => p.Source.AppHostPath = v);
        if (TokenCleared)
            p.Source.GitHubToken = null;
        else if (!string.IsNullOrWhiteSpace(TokenReplacement))
            p.Source.GitHubToken = TokenReplacement.Trim();

        Copy(o.Destination.SubscriptionId, e.Destination.SubscriptionId, v => p.Destination.SubscriptionId = v);
        Copy(o.Destination.TenantId, e.Destination.TenantId, v => p.Destination.TenantId = v);
        Copy(o.Destination.Location, e.Destination.Location, v => p.Destination.Location = v);
        Copy(o.Destination.ResourceGroup, e.Destination.ResourceGroup, v => p.Destination.ResourceGroup = v);
        Copy(o.Destination.CredentialSource, e.Destination.CredentialSource, v => p.Destination.CredentialSource = v);
        if (e.Destination.AllowResourceGroupCreation != o.Destination.AllowResourceGroupCreation)
            p.Destination.AllowResourceGroupCreation = e.Destination.AllowResourceGroupCreation;
        Copy(o.Environment, e.Environment, v => p.Environment = v);
        Copy(o.ContainerRuntime, e.ContainerRuntime, v => p.ContainerRuntime = v);

        // Resource pickers plan D15: a deploy may have switched a section to Existing while this page was open,
        // so only the fields this page changed are written.
        CopyChoice(o.Destination.ContainerRegistry, e.Destination.ContainerRegistry,
            () => p.Destination.ContainerRegistry ??= new(), () => p.Destination.ContainerRegistry);
        CopyChoice(o.Destination.ContainerAppEnvironment, e.Destination.ContainerAppEnvironment,
            () => p.Destination.ContainerAppEnvironment ??= new(), () => p.Destination.ContainerAppEnvironment);
        CopyChoice(o.Destination.AcrPullIdentity, e.Destination.AcrPullIdentity,
            () => p.Destination.AcrPullIdentity ??= new(), () => p.Destination.AcrPullIdentity);
        if (IsEmpty(p.Destination.ContainerRegistry)) p.Destination.ContainerRegistry = null;
        if (IsEmpty(p.Destination.ContainerAppEnvironment)) p.Destination.ContainerAppEnvironment = null;
        if (IsEmpty(p.Destination.AcrPullIdentity)) p.Destination.AcrPullIdentity = null;

        foreach (var removed in _removed.Where(n => !e.Parameters.ContainsKey(n)))
        {
            p.Parameters.Remove(removed);
            p.ParameterConfigKeys.Remove(removed);
            p.SecretParameters.Remove(removed);
        }

        foreach (var (name, value) in e.Parameters)
        {
            if (IsMasked(name))
            {
                if (_replacements.TryGetValue(name, out var r) && !string.IsNullOrWhiteSpace(r))
                    p.Parameters[name] = r;
                else if (_cleared.Contains(name))
                    p.Parameters[name] = "";
            }
            else if (_removed.Contains(name) || !o.Parameters.TryGetValue(name, out var old) || old != value)
            {
                p.Parameters[name] = value;
            }

            var originalKey = o.ParameterConfigKeys.GetValueOrDefault(name);
            var editedKey = e.ParameterConfigKeys.GetValueOrDefault(name);
            if (_removed.Contains(name) || originalKey != editedKey)
            {
                if (editedKey is { Length: > 0 }) p.ParameterConfigKeys[name] = editedKey;
                else p.ParameterConfigKeys.Remove(name);
            }

            // Only toggles this page changed; secret flags another window added are kept. A removed and
            // re-added parameter is new as far as this page is concerned, so its flag is set explicitly.
            var isSecret = _secret.Contains(name);
            if (_removed.Contains(name) || isSecret != o.SecretParameters.Contains(name))
            {
                p.SecretParameters.Remove(name);
                if (isSecret)
                    p.SecretParameters.Add(name);
            }
        }
    }

    private static void CopyChoice(ResourceChoice? original, ResourceChoice? edited, Func<ResourceChoice> target, Func<ResourceChoice?> current)
    {
        var o = original ?? new ResourceChoice();
        var e = edited ?? new ResourceChoice();
        if ((o.Mode ?? ResourceMode.AspireDefault) != (e.Mode ?? ResourceMode.AspireDefault))
        {
            var mode = e.Mode is null or ResourceMode.AspireDefault ? (ResourceMode?)null : e.Mode;
            if (mode is not null || current() is not null)
                target().Mode = mode;
        }
        Copy(o.Name, e.Name, v => target().Name = v);
        Copy(o.ResourceGroup, e.ResourceGroup, v => target().ResourceGroup = v);
        Copy(o.Sku, e.Sku, v => target().Sku = v);
        Copy(o.LogsDestination, e.LogsDestination, v => target().LogsDestination = v);
        Copy(o.AppHostResource, e.AppHostResource, v => target().AppHostResource = v);
        Copy(o.LogsWorkspace?.Name, e.LogsWorkspace?.Name, v => (target().LogsWorkspace ??= new()).Name = v);
        Copy(o.LogsWorkspace?.ResourceGroup, e.LogsWorkspace?.ResourceGroup, v => (target().LogsWorkspace ??= new()).ResourceGroup = v);
        if (current()?.LogsWorkspace is { Name: null, ResourceGroup: null, Extra: null })
            target().LogsWorkspace = null;
    }

    private static bool IsEmpty(ResourceChoice? c) =>
        c is { Mode: null, Name: null, ResourceGroup: null, Sku: null, LogsDestination: null, LogsWorkspace: null, AppHostResource: null, Extra: null };

    private static void Copy(string? original, string? edited, Action<string?> set)
    {
        if (Empty(original) != Empty(edited))
            set(Empty(edited));
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
