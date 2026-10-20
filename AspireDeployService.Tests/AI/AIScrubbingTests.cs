using AspireDeployService.Core.AI;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Settings;

namespace AspireDeployService.Tests.AI;

/// <summary>D9 patterns. Fake keys are assembled at run time so secret scanners do not flag them.</summary>
public class AIRedactionTests
{
    private static string J(params string[] parts) => string.Join("", parts);
    private static string Redact(string line) => new SecretRedactor().Redact(line);

    [Fact]
    public void Masks_ai_keys_tokens_and_headers()
    {
        var sk = J("sk-", "proj-", new string('a', 30));
        var ghp = J("ghp", "_", new string('b', 36));
        var pat = J("github", "_pat_", new string('c', 30));
        var aiza = J("AI", "za", new string('d', 35));
        var hex = new string('e', 32);

        Assert.DoesNotContain(sk, Redact($"key is {sk} ok"));
        Assert.DoesNotContain(ghp, Redact($"token {ghp}"));
        Assert.DoesNotContain(pat, Redact($"token {pat}"));
        Assert.DoesNotContain(aiza, Redact($"?key={aiza}"));
        Assert.Equal("api-key: [REDACTED]", Redact($"api-key: {hex}"));
        Assert.Equal("Ocp-Apim-Subscription-Key: [REDACTED]", Redact($"Ocp-Apim-Subscription-Key: {hex}"));
        Assert.Equal("x-api-key: [REDACTED]", Redact($"x-api-key: {hex}"));
        Assert.Equal("\"apiKey\": \"[REDACTED]\"", Redact($"\"apiKey\": \"{hex}\""));
        Assert.Equal("\"ApiKey\": \"[REDACTED]\"", Redact($"\"ApiKey\": \"{hex}\""));
        Assert.Equal("OPENAI_API_KEY=[REDACTED]", Redact($"OPENAI_API_KEY={hex}"));
        Assert.Equal("AZURE_OPENAI_API_KEY=[REDACTED]", Redact($"AZURE_OPENAI_API_KEY={hex}"));
        Assert.Equal("GEMINI_API_KEY=[REDACTED]", Redact($"GEMINI_API_KEY={hex}"));
    }

    [Fact]
    public void Ordinary_text_is_kept()
    {
        const string line = "error NU1605: Detected package downgrade: Microsoft.Extensions.AI from 10.11.0 to 10.10.0";
        Assert.Equal(line, Redact(line));
    }
}

public class AIPromptScrubberTests
{
    private static SettingsDocument Document()
    {
        var doc = new SettingsDocument();
        doc.AspireDeploy.GitHub = new GitHubSection { Token = "global-gh-token" };
        doc.AspireDeploy.Profiles["shop"] = new DeployProfile
        {
            Source = new ProfileSource { GitHubToken = "profile-gh-token" },
            Parameters = { ["apiKey"] = "  spaced-secret  ", ["pin"] = "1", ["region"] = "westus" },
            SecretParameters = ["apiKey", "pin"]
        };
        doc.AspireDeploy.AI = new AISection();
        doc.AspireDeploy.AI.Providers["OpenAI"] = new AIProviderSection { ApiKey = "openai-key-value" };
        doc.AspireDeploy.AI.Providers["Ollama"] = new AIProviderSection { ApiKey = "unknown-provider-key" };
        return doc;
    }

    [Fact]
    public void Every_settings_secret_is_masked()
    {
        var scrubber = AIPromptScrubber.Create(Document());
        var text = "a global-gh-token b profile-gh-token c spaced-secret d openai-key-value e unknown-provider-key\nregion westus 1";
        var scrubbed = scrubber.Scrub(text);
        foreach (var secret in new[] { "global-gh-token", "profile-gh-token", "spaced-secret", "openai-key-value", "unknown-provider-key" })
            Assert.DoesNotContain(secret, scrubbed);
        // A non-secret parameter and a 1-character secret are left alone.
        Assert.EndsWith("region westus 1", scrubbed);
    }

    [Fact]
    public void Pem_block_over_several_lines_is_removed()
    {
        var scrubbed = AIPromptScrubber.Create(new SettingsDocument()).Scrub(
            "before\n-----BEGIN PRIVATE KEY-----\nMIIabc\n-----END PRIVATE KEY-----\nafter");
        Assert.DoesNotContain("MIIabc", scrubbed);
        Assert.StartsWith("before", scrubbed);
        Assert.EndsWith("after", scrubbed);
    }

    [Fact]
    public async Task Reads_the_current_file_each_time()
    {
        using var dir = new Support.TempDir();
        var path = dir.Combine("appsettings.Local.json");
        File.WriteAllText(path, """{ "AspireDeploy": { "GitHub": { "Token": "first-token" } } }""");
        var scrubber = new AIPromptScrubber(new ProfileStore(new FixedLocator(path)));
        Assert.DoesNotContain("first-token", (await scrubber.CreateAsync(CancellationToken.None)).Scrub("first-token"));

        File.WriteAllText(path, """{ "AspireDeploy": { "GitHub": { "Token": "second-token" } } }""");
        Assert.DoesNotContain("second-token", (await scrubber.CreateAsync(CancellationToken.None)).Scrub("second-token"));
    }
}

public class AIContextFormatterTests
{
    private static AIHelpContext Context(string summary, int lines, Func<int, string>? text = null) => new(
        "id", "Discovery failed", AIContextKind.RunResult, summary,
        [new("Page", "Discover"), new("Result", "DiscoveryFailed (4)")],
        Enumerable.Range(1, lines).Select(i => new LogLine(DateTimeOffset.UtcNow, "stdout", text?.Invoke(i) ?? $"line {i}")).ToList(),
        null, null);

    [Fact]
    public void Tail_length_follows_the_setting_and_keeps_earlier_errors()
    {
        var text = AIContextFormatter.Format(Context("boom", 500, i => i == 10 ? "error NU1605 at line 10" : $"line {i}"), 20);
        Assert.Contains("# Discovery failed", text);
        Assert.Contains("- Result: DiscoveryFailed (4)", text);
        Assert.Contains("error NU1605 at line 10", text);
        Assert.Contains("line 500", text);
        Assert.Contains("line 481", text);
        Assert.DoesNotContain("line 480\n", text);
        Assert.Contains("(last 20 lines)", text);
    }

    [Fact]
    public void Long_summary_keeps_head_and_tail()
    {
        var summary = "HEAD" + new string('x', 30_000) + "TAIL";
        var text = AIContextFormatter.Format(Context(summary, 0), 200);
        Assert.Contains("HEAD", text);
        Assert.Contains("TAIL", text);
        Assert.Contains("characters cut", text);
        Assert.True(text.Length < 9_000);
    }

    [Fact]
    public void Output_never_exceeds_the_cap()
    {
        var text = AIContextFormatter.Format(Context(new string('s', 8_000), 1000, i => $"line {i} " + new string('y', 200)), 1000);
        Assert.True(text.Length <= AIContextFormatter.MaxContextChars);
        Assert.Contains("line 1000 ", text);
        Assert.EndsWith("```\n", text);
    }
}

public class AIReplyFormatterTests
{
    [Fact]
    public void Plain_text_is_one_segment()
    {
        var segment = Assert.Single(AIReplyFormatter.Split("Hello `world`."));
        Assert.False(segment.IsCode);
        Assert.Equal(["Hello ", "world", "."], AIReplyFormatter.SplitInlineCode(segment.Text));
    }

    [Fact]
    public void Fenced_blocks_with_language_tags()
    {
        var segments = AIReplyFormatter.Split("Change this:\n```xml\n<A />\n```\nand:\n```\nB\n```\nDone.");
        Assert.Equal(5, segments.Count);
        Assert.Equal(new ReplySegment(true, "xml", "<A />"), segments[1]);
        Assert.Equal(new ReplySegment(false, null, "and:"), segments[2]);
        Assert.Equal(new ReplySegment(true, null, "B"), segments[3]);
        Assert.Equal("Done.", segments[4].Text);
    }

    [Fact]
    public void Unclosed_fence_runs_to_the_end()
    {
        var segments = AIReplyFormatter.Split("Try:\n```bash\ndotnet restore\n");
        Assert.Equal(2, segments.Count);
        Assert.True(segments[1].IsCode);
        Assert.Equal("bash", segments[1].Language);
        Assert.Contains("dotnet restore", segments[1].Text);
    }
}
