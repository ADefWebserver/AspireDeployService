using System.Text;
using System.Text.RegularExpressions;

namespace AspireDeployService.Core.AI;

/// <param name="Anchor">The in-app Guide page's anchor (resource pickers plan §6.7), for example "azure-resources".</param>
public sealed record GuideSection(string Anchor, string Title, string Text);

/// <summary>
/// The embedded <c>docs/User-Guide.md</c> split into its "## " sections (resource pickers plan §6.8, D16). A section's
/// anchor is the explicit <c>&lt;a id="..."&gt;</c> line right above its heading, or else the heading's GitHub-style slug.
/// </summary>
public static partial class UserGuide
{
    public const string ResourceName = "AspireDeployService.Core.UserGuide.md";

    private static readonly Lazy<IReadOnlyList<GuideSection>> _sections = new(() => Parse(Load()));

    [GeneratedRegex(@"^<a\s+id\s*=\s*""(?<id>[A-Za-z0-9_-]+)""\s*>\s*</a>\s*$")]
    private static partial Regex AnchorLine();

    public static IReadOnlyList<GuideSection> Sections => _sections.Value;

    /// <summary>null when the anchor or title (case-insensitive) is unknown.</summary>
    public static GuideSection? Find(string? anchorOrTitle)
    {
        var key = (anchorOrTitle ?? "").Trim().TrimStart('#');
        if (key.Length == 0)
            return null;
        return Sections.FirstOrDefault(s => s.Anchor.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? Sections.FirstOrDefault(s => s.Title.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? Sections.FirstOrDefault(s => Slug(s.Title).Equals(Slug(key), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"anchor: title" per line, for the system prompt.</summary>
    public static string SectionList() => string.Join('\n', Sections.Select(s => $"{s.Anchor}: {s.Title}"));

    internal static string Load()
    {
        using var stream = typeof(UserGuide).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static IReadOnlyList<GuideSection> Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sections = new List<GuideSection>();
        string? anchor = null, title = null, pendingAnchor = null;
        var body = new StringBuilder();
        var inFence = false;

        void Flush()
        {
            if (title is not null)
                sections.Add(new GuideSection(anchor ?? Slug(title), title, body.ToString().Trim()));
            body.Clear();
        }

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;
            if (!inFence && AnchorLine().Match(line.Trim()) is { Success: true } a)
            {
                pendingAnchor = a.Groups["id"].Value;
                continue;
            }
            if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                title = line[3..].Trim();
                anchor = pendingAnchor;
                pendingAnchor = null;
                continue;
            }
            pendingAnchor = null;
            if (title is not null)
                body.AppendLine(line);
        }
        Flush();
        return sections;
    }

    /// <summary>GitHub's heading slug: lowercase, punctuation dropped, spaces to dashes.</summary>
    internal static string Slug(string heading)
    {
        var sb = new StringBuilder();
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('-');
        }
        return sb.ToString();
    }
}
