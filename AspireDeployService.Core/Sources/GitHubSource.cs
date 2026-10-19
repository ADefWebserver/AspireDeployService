using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AspireDeployService.Core.Archives;
using AspireDeployService.Core.Errors;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;

namespace AspireDeployService.Core.Sources;

/// <summary>
/// Source plan D10 and §7.5: resolves the ref to a commit SHA, downloads that commit's zipball, extracts it with
/// <see cref="SafeZip"/> and moves the single top-level folder to the fixed <c>&lt;WorkKey&gt;/src</c>, which keeps
/// Aspire's path-keyed deploy cache stable between commits.
/// </summary>
public sealed partial class GitHubSource(IHttpClientFactory httpFactory) : ISourceProvider
{
    public const string HttpClientName = "github";
    private const string ShaMarker = ".ads-source-sha";

    /// <summary>The token to send; set by the caller from <see cref="GitHubTokenResolver"/> before materializing.</summary>
    public string? Token { get; set; }

    public long MaxDownloadBytes { get; set; } = 512L * 1024 * 1024;

    public SourceKind Kind => SourceKind.GitHub;

    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)$")]
    private static partial Regex RepoPattern();

    public async Task<SourceIdentity> MaterializeAsync(SourceSpec spec, string workKeyDir, RunServices run, CancellationToken ct)
    {
        var m = RepoPattern().Match(spec.Location.Trim());
        if (!m.Success)
            throw new DeployerException(OutcomeCode.InvalidInput, $"'{spec.Location}' is not a GitHub repository in the form <owner>/<repo>.");
        var repo = $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}";
        var gitRef = string.IsNullOrWhiteSpace(spec.Ref) ? "HEAD" : spec.Ref.Trim();

        var http = httpFactory.CreateClient(HttpClientName);
        var sha = await ResolveShaAsync(http, repo, gitRef, ct);
        var display = $"{repo}@{(gitRef == "HEAD" ? "default branch" : gitRef)} ({sha[..7]})";
        var src = Path.Combine(workKeyDir, "src");

        if (File.Exists(Path.Combine(src, ShaMarker)) && (await File.ReadAllTextAsync(Path.Combine(src, ShaMarker), ct)).Trim() == sha)
        {
            await run.Log.InfoAsync($"Source {display} is already downloaded.", ct);
            return new SourceIdentity(src, display, sha);
        }

        await run.Log.InfoAsync($"Downloading {display}.", ct);
        var zip = Path.Combine(workKeyDir, "download", sha + ".zip");
        await DownloadAsync(http, repo, sha, zip, ct);

        var extract = Path.Combine(workKeyDir, "extract");
        try
        {
            await DeleteWithRetriesAsync(extract, ct);
            string inner;
            try
            {
                SafeZip.ExtractToDirectory(zip, extract);
                inner = SafeZip.GetSingleTopLevelFolder(extract);
            }
            catch (InvalidDataException ex)
            {
                throw new DeployerException(OutcomeCode.DiscoveryFailed, "The GitHub archive is unsafe or malformed: " + ex.Message, ErrorCategory.Source, ex);
            }

            await DeleteWithRetriesAsync(src, ct);
            Directory.Move(inner, src);
            await File.WriteAllTextAsync(Path.Combine(src, ShaMarker), sha, ct);
        }
        finally
        {
            TryDelete(extract);
            TryDeleteFile(zip);
        }
        return new SourceIdentity(src, display, sha);
    }

    private async Task<string> ResolveShaAsync(HttpClient http, string repo, string gitRef, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"https://api.github.com/repos/{repo}/commits/{Uri.EscapeDataString(gitRef)}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw Failure(response, repo, gitRef);

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        var sha = doc.RootElement.TryGetProperty("sha", out var s) ? s.GetString() : null;
        if (sha is null || !ShaPattern().IsMatch(sha))
            throw new DeployerException(OutcomeCode.InvalidInput, $"GitHub did not return a commit for {repo}@{gitRef}.");
        return sha;
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex ShaPattern();

    private DeployerException Failure(HttpResponseMessage response, string repo, string gitRef)
    {
        var status = (int)response.StatusCode;
        var remaining = Header(response, "x-ratelimit-remaining");
        var message = status switch
        {
            404 when Token is null => $"{repo} or '{gitRef}' was not found. The repository may be private: add a GitHub token (Settings page or the profile).",
            404 => $"{repo} or '{gitRef}' was not found, or the token cannot see it. The token needs Contents: read on this repository.",
            403 or 429 when remaining == "0" => $"GitHub's rate limit is used up. Try again after {ResetTime(response)}, or add a GitHub token.",
            403 when Header(response, "X-GitHub-SSO") is not null => "The token must be authorized for the organization's single sign-on (SAML). Authorize it on GitHub, then try again.",
            401 or 403 => "GitHub refused the token. It needs Contents: read on this repository.",
            422 => $"'{gitRef}' is not a branch, tag or commit of {repo}.",
            _ => $"GitHub returned {status} {response.ReasonPhrase} for {repo}@{gitRef}."
        };
        return new DeployerException(OutcomeCode.InvalidInput, message);
    }

    private async Task DownloadAsync(HttpClient http, string repo, string sha, string zipPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        // GitHub answers with a 302 to codeload.github.com; HttpClient drops Authorization on that cross-host redirect.
        using var request = Request(HttpMethod.Get, $"https://api.github.com/repos/{repo}/zipball/{sha}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw Failure(response, repo, sha);
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            throw TooLarge();

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
                throw TooLarge();
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private DeployerException TooLarge() =>
        new(OutcomeCode.DiscoveryFailed, $"The GitHub archive is larger than {MaxDownloadBytes / (1024 * 1024)} MB.", ErrorCategory.Source);

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AspireDeployService", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (Token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string ResetTime(HttpResponseMessage r) =>
        long.TryParse(Header(r, "x-ratelimit-reset"), out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("t")
            : "the reset time";

    /// <summary>Deletes a folder, retrying 3 times 1 second apart when files are locked (code 4 after that).</summary>
    internal static async Task DeleteWithRetriesAsync(string dir, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 3)
                    throw new DeployerException(OutcomeCode.DiscoveryFailed,
                        $"Files in {dir} are in use (an editor, terminal or running app may hold them). Close it and try again.", ErrorCategory.Source, ex);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next run, which deletes it before extracting.
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Harmless leftover.
        }
    }
}
