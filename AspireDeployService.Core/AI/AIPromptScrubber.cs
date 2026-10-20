using System.Text;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Core.AI;

/// <summary>Masks secrets in text that is about to leave the machine (AI Help plan D8).</summary>
public interface IPromptRedactor
{
    /// <summary>Scrubs multi-line text line by line; a PEM block spread over lines is removed whole.</summary>
    string Scrub(string text);
}

/// <summary>
/// Builds the one scrubber used for everything AI Help sends (D8): a <see cref="DeploySecretRedactor"/> that knows every
/// secret in the settings file (the global GitHub token, each profile's GitHub token and secret parameters, every AI
/// key) plus the pattern rules. Rebuilt on every send, so a secret changed after a chat started is still masked.
/// </summary>
public sealed class AIPromptScrubber(ProfileStore store)
{
    /// <summary>Shorter values would wipe ordinary words (a secret parameter of "1" would mask every digit).</summary>
    public const int MinimumSecretLength = 4;

    public async Task<IPromptRedactor> CreateAsync(CancellationToken ct)
    {
        var snapshot = await store.LoadAsync(ct);
        return Create(snapshot.Document);
    }

    public static IPromptRedactor Create(SettingsDocument document, params string?[] extraSecrets)
    {
        var redactor = new DeploySecretRedactor();
        foreach (var secret in SecretsIn(document).Concat(extraSecrets))
        {
            if (secret is null || secret.Trim().Length < MinimumSecretLength)
                continue;
            redactor.AddSecret(secret);
            redactor.AddSecret(secret.Trim());
        }
        return new Redactor(redactor);
    }

    internal static IEnumerable<string?> SecretsIn(SettingsDocument document)
    {
        var section = document.AspireDeploy;
        yield return section.GitHub?.Token;
        foreach (var profile in section.Profiles.Values)
        {
            yield return profile.Source.GitHubToken;
            foreach (var name in profile.SecretParameters)
                yield return profile.Parameters.GetValueOrDefault(name);
        }
        if (section.AI is { } ai)
        {
            foreach (var provider in ai.Providers.Values)
                yield return provider?.ApiKey;
        }
    }

    private sealed class Redactor(ISecretRedactor inner) : IPromptRedactor
    {
        public string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            var scope = inner.CreateScope();
            var output = new StringBuilder(text.Length);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var cr = line.EndsWith('\r');
                if (scope.Redact(cr ? line[..^1] : line) is { } kept)
                {
                    output.Append(kept);
                    if (cr)
                        output.Append('\r');
                    if (i < lines.Length - 1)
                        output.Append('\n');
                }
            }
            return output.ToString();
        }
    }
}
