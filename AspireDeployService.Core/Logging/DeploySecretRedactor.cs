using System.Collections.Concurrent;

namespace AspireDeployService.Core.Logging;

/// <summary>
/// The per-run redactor (source plan §6.7, plan §5.2). Wraps <see cref="SecretRedactor"/> and also masks
/// registered secrets shorter than 8 characters, which the wrapped redactor ignores. One instance per run,
/// so one run's secrets never mask another run's output.
/// </summary>
public sealed class DeploySecretRedactor : ISecretRedactor
{
    private readonly SecretRedactor _inner = new();
    private readonly ConcurrentDictionary<string, byte> _short = new(StringComparer.Ordinal);

    public void AddSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        AddOne(value);
        // ProcessRunner redacts output line by line, so a multi-line secret is also registered per line.
        if (value.Contains('\n') || value.Contains('\r'))
        {
            foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddOne(line);
        }
    }

    private void AddOne(string value)
    {
        _inner.AddSecret(value);
        if (value.Length < 8)
            _short.TryAdd(value, 0);
    }

    public RedactionScope CreateScope() => new(this);

    public string Redact(string line)
    {
        line = _inner.Redact(line);
        if (_short.IsEmpty || string.IsNullOrEmpty(line))
            return line;
        foreach (var secret in _short.Keys.OrderByDescending(s => s.Length))
        {
            if (line.Contains(secret, StringComparison.Ordinal))
                line = line.Replace(secret, SecretRedactor.Mask, StringComparison.Ordinal);
        }
        return line;
    }
}
