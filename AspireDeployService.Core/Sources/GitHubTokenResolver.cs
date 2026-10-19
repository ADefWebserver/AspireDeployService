using System.ComponentModel;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;

namespace AspireDeployService.Core.Sources;

public sealed record GitHubToken(string? Value, string Origin);

/// <summary>
/// Source plan §5.2: the profile's <c>Source:GitHubToken</c>, then <c>AspireDeploy:GitHub:Token</c>, then
/// <c>gh auth token</c>, then no token. Whatever is found is registered with the run's redactor.
/// </summary>
public sealed class GitHubTokenResolver
{
    public async Task<GitHubToken> ResolveAsync(string? profileToken, string? globalToken, RunContext run, CancellationToken ct)
    {
        var token = !string.IsNullOrWhiteSpace(profileToken) ? new GitHubToken(profileToken.Trim(), "the profile")
            : !string.IsNullOrWhiteSpace(globalToken) ? new GitHubToken(globalToken.Trim(), "the settings file")
            : await FromGhAsync(run, ct);
        run.Redactor.AddSecret(token.Value);
        return token;
    }

    private static async Task<GitHubToken> FromGhAsync(RunContext run, CancellationToken ct)
    {
        try
        {
            var spec = new ProcessSpec("gh", ["auth", "token"], Path.GetTempPath())
            {
                CaptureStdOut = true,
                LogStdOut = false,
                Timeout = TimeSpan.FromSeconds(30)
            };
            var result = await run.Runner.RunAsync(spec, run.Log, ct);
            var value = result.Succeeded ? result.StdOut.FirstOrDefault()?.Trim() : null;
            return string.IsNullOrEmpty(value) ? new GitHubToken(null, "none") : new GitHubToken(value, "gh auth token");
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException)
        {
            return new GitHubToken(null, "none");
        }
    }
}
