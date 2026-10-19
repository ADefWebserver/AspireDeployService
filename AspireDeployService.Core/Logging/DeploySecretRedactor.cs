namespace AspireDeployService.Core.Logging;

/// <summary>
/// The per-run redactor (source plan §6.7, plan §5.2). Masks every registered secret itself, including ones shorter
/// than 8 characters, which the wrapped <see cref="SecretRedactor"/> ignores; the wrapped redactor only adds its
/// pattern rules (JWTs, PEM blocks, key=value pairs). One instance per run, so one run's secrets never mask
/// another run's output.
/// </summary>
public sealed class DeploySecretRedactor : ISecretRedactor
{
    private readonly SecretRedactor _inner = new();
    private readonly Lock _gate = new();
    private string[] _secrets = []; // longest first; replaced, never mutated, so Redact reads it without the lock

    public void AddSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        var added = new List<string> { value };
        // ProcessRunner redacts output line by line, so a multi-line secret is also registered per line.
        if (value.Contains('\n') || value.Contains('\r'))
            added.AddRange(value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        lock (_gate)
        {
            // Longest first: with "abcdefgh" and "abcdefgh-tail" registered, masking the shorter one first would
            // leave "[REDACTED]-tail" behind, where the longer secret no longer matches.
            _secrets = _secrets.Concat(added).Distinct(StringComparer.Ordinal)
                .OrderByDescending(s => s.Length).ThenBy(s => s, StringComparer.Ordinal).ToArray();
        }
    }

    public RedactionScope CreateScope() => new(this);

    public string Redact(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;
        foreach (var secret in _secrets)
        {
            if (line.Contains(secret, StringComparison.Ordinal))
                line = line.Replace(secret, SecretRedactor.Mask, StringComparison.Ordinal);
        }
        return _inner.Redact(line);
    }
}
