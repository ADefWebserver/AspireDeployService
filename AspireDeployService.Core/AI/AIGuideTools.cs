using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace AspireDeployService.Core.AI;

/// <summary>The <c>read_guide</c> tool (resource pickers plan §6.8, D16): one section of the user guide, scrubbed and capped.</summary>
public sealed class AIGuideTools(IPromptRedactor redactor)
{
    public const int MaxSectionChars = 8_000;

    [Description("Read a section of the Aspire Deploy user guide. Pass a section anchor or title from the list in the system prompt.")]
    public string ReadGuide([Description("A section anchor such as \"azure-resources\", or its title.")] string section)
    {
        if (UserGuide.Find(section) is not { } found)
            return "Unknown section. Use one of: " + string.Join(", ", UserGuide.Sections.Select(s => s.Anchor));
        var text = $"## {found.Title}\n\n{found.Text}";
        if (text.Length > MaxSectionChars)
            text = text[..MaxSectionChars] + "\n(cut; the rest is in the app's Guide page)";
        return redactor.Scrub(text);
    }

    /// <summary>One tool named read_guide.</summary>
    public IList<AITool> AsTools() =>
        [AIFunctionFactory.Create((Func<string, string>)ReadGuide, new AIFunctionFactoryOptions { Name = "read_guide" })];
}
