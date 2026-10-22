using AspireDeployService.Core.AI;
using AspireDeployService.Core.Azure;

namespace AspireDeployService.Tests.AI;

public class UserGuideTests
{
    // Resource pickers plan §6.7: the in-app Guide page uses the same anchors.
    private static readonly string[] Anchors = ["before", "how", "profile", "azure-resources", "apphost-kinds", "deploy", "redeploy", "remove", "trouble"];

    private sealed class PassThrough : IPromptRedactor
    {
        public string Scrub(string text) => text.Replace("secret-value", "[REDACTED]");
    }

    [Fact]
    public void Every_plan_anchor_is_a_section_of_the_embedded_guide()
    {
        var anchors = UserGuide.Sections.Select(s => s.Anchor).ToList();
        foreach (var anchor in Anchors)
            Assert.Contains(anchor, anchors);
        Assert.All(UserGuide.Sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
    }

    [Fact]
    public void Find_works_by_anchor_title_and_slug()
    {
        Assert.Equal("azure-resources", UserGuide.Find("azure-resources")!.Anchor);
        Assert.Equal("azure-resources", UserGuide.Find("#AZURE-RESOURCES")!.Anchor);
        Assert.Equal("trouble", UserGuide.Find("troubleshooting")!.Anchor);
        Assert.Equal("before", UserGuide.Find("Before you start")!.Anchor);
        Assert.Null(UserGuide.Find("no-such-section"));
        Assert.Null(UserGuide.Find(""));
    }

    [Fact]
    public void Parse_uses_explicit_anchors_skips_code_fences_and_falls_back_to_slugs()
    {
        var sections = UserGuide.Parse("""
            # Title
            intro
            <a id="first"></a>
            ## 1. First thing
            text one
            ```bash
            ## not a heading
            ```
            ## Second Thing!
            text two
            """.Replace("            ", ""));
        Assert.Equal(["first", "second-thing"], sections.Select(s => s.Anchor));
        Assert.Contains("## not a heading", sections[0].Text);
        Assert.Equal("text two", sections[1].Text);
    }

    [Fact]
    public void The_naming_table_matches_the_rules_in_code()
    {
        var section = UserGuide.Find("azure-resources")!.Text;
        foreach (var (_, rule, _) in AzureNameRules.Describe())
            Assert.Contains(rule, section);
    }

    [Fact]
    public void Read_guide_caps_scrubs_and_lists_anchors_for_an_unknown_section()
    {
        var tools = new AIGuideTools(new PassThrough());
        var text = tools.ReadGuide("trouble");
        Assert.StartsWith("## Troubleshooting", text);
        Assert.True(text.Length <= AIGuideTools.MaxSectionChars + 60);

        var unknown = tools.ReadGuide("nope");
        Assert.StartsWith("Unknown section.", unknown);
        Assert.Contains("azure-resources", unknown);

        Assert.Equal("read_guide", Assert.Single(tools.AsTools()).Name);
    }
}
