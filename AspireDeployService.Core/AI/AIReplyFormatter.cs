namespace AspireDeployService.Core.AI;

/// <summary>One piece of an assistant reply: plain text, or a fenced code block.</summary>
public sealed record ReplySegment(bool IsCode, string? Language, string Text);

/// <summary>
/// Splits a reply into text and fenced-code segments (AI Help plan §6.6). The panel renders each segment as a Razor
/// element, never as markup, so nothing the model writes becomes HTML. An unclosed fence (a reply still streaming)
/// runs to the end of the text.
/// </summary>
public static class AIReplyFormatter
{
    public static IReadOnlyList<ReplySegment> Split(string reply)
    {
        var segments = new List<ReplySegment>();
        var current = new List<string>();
        string? language = null;
        var inCode = false;

        void Flush()
        {
            var text = string.Join('\n', current);
            if (inCode || text.Trim().Length > 0)
                segments.Add(new ReplySegment(inCode, language, inCode ? text : text.Trim('\n')));
            current.Clear();
        }

        foreach (var raw in reply.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCode)
                {
                    Flush();
                    inCode = true;
                    var tag = trimmed[3..].Trim();
                    language = tag.Length == 0 ? null : tag;
                }
                else
                {
                    Flush();
                    inCode = false;
                    language = null;
                }
                continue;
            }
            current.Add(raw);
        }
        if (inCode || current.Count > 0)
            Flush();
        return segments;
    }

    /// <summary>Splits a text segment on `inline code` spans: odd entries are code.</summary>
    public static IReadOnlyList<string> SplitInlineCode(string text) => text.Split('`');
}
