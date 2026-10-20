using System.Collections.Concurrent;
using System.Text.RegularExpressions;

// Copied from BlazorDataDeploymentWebsite (implementation plan D2): DeployWorker/Logging/SecretRedactor.cs.
namespace AspireDeployService.Core.Logging;

/// <summary>Removes credentials from text before it reaches the UI, the run log file or ILogger.</summary>
public interface ISecretRedactor
{
    /// <summary>Redacts one line. Multi-line PEM blocks need a <see cref="CreateScope"/> instead.</summary>
    string Redact(string line);

    /// <summary>Registers a runtime secret (client secret, federated assertion) to be masked wherever it appears.</summary>
    void AddSecret(string? value);

    /// <summary>A stateful filter for one output stream, so a PEM block split over many lines is suppressed entirely.</summary>
    RedactionScope CreateScope();
}

public sealed partial class SecretRedactor : ISecretRedactor
{
    public const string Mask = "[REDACTED]";

    private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_\-+/=]*")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----.*?(-----END [A-Z0-9 ]+-----|$)", RegexOptions.Singleline)]
    private static partial Regex PemPattern();

    // Connection-string style key=value pairs.
    [GeneratedRegex(@"\b(AccountKey|SharedAccessKey|SharedAccessSignature|Password|Pwd|ClientSecret|client_secret|client_assertion|access_token|refresh_token|id_token)\s*=\s*(""[^""]*""|'[^']*'|[^;&\s""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValuePattern();

    // SAS signatures in URLs and connection strings.
    [GeneratedRegex(@"([?&;]|\b)sig=[^&\s""';]+", RegexOptions.IgnoreCase)]
    private static partial Regex SasSignaturePattern();

    // Secret-bearing command-line flags (echoed commands, az/azd debug output).
    [GeneratedRegex(@"(--client-secret|--password|--federated-token|--client-assertion|--secret)(\s+|=)(""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretFlagPattern();

    // JSON properties.
    [GeneratedRegex(@"""(client_?secret|clientSecret|password|access_?token|accessToken|refresh_?token|refreshToken|id_token|client_assertion|federatedToken|accountKey|primaryKey|secondaryKey|connectionString|api_?key)""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretPattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    // Environment dumps (KEY=value or KEY: value).
    [GeneratedRegex(@"\b(AZURE_OIDC_TOKEN|AZURE_OIDC_REQUEST_TOKEN|AZURE_CLIENT_SECRET|AZURE_FEDERATED_TOKEN|ARM_CLIENT_SECRET|GITHUB_TOKEN|GitHub__Token|AspireDeploy__GitHub__Token|OPENAI_API_KEY|AZURE_OPENAI_API_KEY|ANTHROPIC_API_KEY|GEMINI_API_KEY|GOOGLE_API_KEY)\s*[=:]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretEnvPattern();

    // AI Help plan D9: API key headers (HTTP dumps, curl -v).
    [GeneratedRegex(@"\b(x-api-key|Ocp-Apim-Subscription-Key|api-key)\s*:\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyHeaderPattern();

    // AI Help plan D9: tokens recognisable by shape alone. OpenAI/Anthropic "sk-" keys, GitHub tokens, Google API keys.
    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{20,}|\bgh[pousr]_[A-Za-z0-9]{36,}|\bgithub_pat_[A-Za-z0-9_]{22,}|\bAIza[0-9A-Za-z_-]{35}")]
    private static partial Regex KnownTokenPattern();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----")]
    internal static partial Regex PemBeginPattern();

    [GeneratedRegex(@"-----END [A-Z0-9 ]+-----")]
    internal static partial Regex PemEndPattern();

    public void AddSecret(string? value)
    {
        // Very short values would mask ordinary words, and they are not real secrets anyway.
        if (!string.IsNullOrWhiteSpace(value) && value.Length >= 8)
            _secrets.TryAdd(value, 0);
    }

    public RedactionScope CreateScope() => new(this);

    public string Redact(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        foreach (var secret in _secrets.Keys)
        {
            if (line.Contains(secret, StringComparison.Ordinal))
                line = line.Replace(secret, Mask, StringComparison.Ordinal);
        }

        line = PemPattern().Replace(line, "[REDACTED-PEM]");
        line = JwtPattern().Replace(line, "[REDACTED-JWT]");
        line = BearerPattern().Replace(line, "Bearer " + Mask);
        line = KnownTokenPattern().Replace(line, Mask);
        line = ApiKeyHeaderPattern().Replace(line, m => m.Groups[1].Value + ": " + Mask);
        line = JsonSecretPattern().Replace(line, m => $"\"{m.Groups[1].Value}\": \"{Mask}\"");
        line = SecretFlagPattern().Replace(line, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        line = SecretEnvPattern().Replace(line, m => m.Groups[1].Value + "=" + Mask);
        line = KeyValuePattern().Replace(line, m => m.Groups[1].Value + "=" + Mask);
        line = SasSignaturePattern().Replace(line, m => m.Groups[1].Value + "sig=" + Mask);
        return line;
    }
}

/// <summary>Per-stream redaction state: suppresses every line between a PEM BEGIN and END marker.</summary>
public sealed class RedactionScope(ISecretRedactor redactor)
{
    private bool _inPem;

    /// <summary>Returns the redacted line, or null when the whole line belongs to a suppressed PEM block.</summary>
    public string? Redact(string line)
    {
        if (_inPem)
        {
            if (SecretRedactor.PemEndPattern().IsMatch(line))
                _inPem = false;
            return null;
        }

        var begin = SecretRedactor.PemBeginPattern().Match(line);
        if (begin.Success && !SecretRedactor.PemEndPattern().IsMatch(line, begin.Index))
        {
            _inPem = true;
            return redactor.Redact(line[..begin.Index]) + "[REDACTED-PEM]";
        }

        return redactor.Redact(line);
    }
}
