using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BaryoDev.Umbraco.ReadAloud.Content;

/// <summary>
/// Turns a rich text property into something worth speaking.
/// </summary>
public static partial class TextExtractor
{
    /// <summary>
    /// Strips markup, decodes entities, collapses whitespace and truncates on a word boundary.
    /// </summary>
    /// <remarks>
    /// Truncation cuts at a space rather than mid-word, because a voice reading half a word is
    /// the kind of detail that makes a whole feature feel broken.
    /// </remarks>
    public static string ToSpeakableText(string? html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = StripScriptAndStyle(html);
        text = BlockTags().Replace(text, " ");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = Whitespace().Replace(text, " ").Trim();

        if (text.Length <= maxChars) return text;

        // A space far back in the budget is no use: "Hi " followed by 20000 CJK characters has no
        // space after index 2, and cutting there would speak two characters of the article. Past
        // half the budget the word is long enough to cut inside, on a code point boundary.
        var cut = text.LastIndexOf(' ', Math.Min(maxChars, text.Length - 1));
        if (cut > 0 && cut >= maxChars / 2) return text[..cut].TrimEnd();

        var end = maxChars;
        if (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end].TrimEnd();
    }

    /// <summary>
    /// Removes <c>&lt;script&gt;...&lt;/script&gt;</c> and <c>&lt;style&gt;...&lt;/style&gt;</c>
    /// blocks, replacing each with a space.
    /// </summary>
    /// <remarks>
    /// A scan rather than a regex, because the lazy <c>.*?</c> version is quadratic on an opener
    /// with no closer: every opener rescans to the end of the text. Here the position of the next
    /// <c>&gt;</c> and of each closer is remembered, so a search never repeats over the same text.
    /// An opener with no closer is left in place, as before, and the tag pass removes it.
    /// </remarks>
    private static string StripScriptAndStyle(string html)
    {
        if (html.AsSpan().IndexOf('<') < 0) return html;

        var sb = new StringBuilder(html.Length);
        var pos = 0;
        var gt = -2;            // last '>' found, -1 for none left
        var closeScript = -2;   // last "</script>" found, -1 for none left
        var closeStyle = -2;

        while (pos < html.Length)
        {
            var lt = html.IndexOf('<', pos);
            if (lt < 0) break;

            var name = Name(html, lt + 1);
            if (name is null)
            {
                sb.Append(html, pos, lt + 1 - pos);
                pos = lt + 1;
                continue;
            }

            if (gt != -1 && gt < lt) gt = html.IndexOf('>', lt);
            if (gt < 0) break; // no '>' anywhere after this, so nothing further can match

            ref var close = ref (name == "script" ? ref closeScript : ref closeStyle);
            var closer = $"</{name}>";
            if (close != -1 && close < gt + 1)
                close = html.IndexOf(closer, gt + 1, StringComparison.OrdinalIgnoreCase);

            if (close < 0)
            {
                sb.Append(html, pos, lt + 1 - pos);
                pos = lt + 1;
                continue;
            }

            sb.Append(html, pos, lt - pos).Append(' ');
            pos = close + closer.Length;
        }

        if (pos < html.Length) sb.Append(html, pos, html.Length - pos);
        return sb.ToString();
    }

    /// <summary>"script" or "style" if one starts at <paramref name="start"/> and ends on a word boundary.</summary>
    private static string? Name(string html, int start)
    {
        foreach (var name in new[] { "script", "style" })
        {
            if (string.Compare(html, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
            var next = start + name.Length;
            if (next >= html.Length || !(char.IsLetterOrDigit(html[next]) || html[next] == '_')) return name;
        }

        return null;
    }

    // Block elements become a space so words either side do not run together, while inline
    // elements are removed outright so punctuation stays attached to the word before it.
    [GeneratedRegex(@"</?(p|div|br|li|h[1-6]|tr|td|th|section|article|header|footer|blockquote|pre|ul|ol|table|dl|dt|dd|figure|figcaption|main|aside|hr)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex BlockTags();

    // NonBacktracking on both tag regexes, because an unclosed "<" with no ">" after it makes the
    // backtracking engine rescan to the end of the text for every opener, which is quadratic.
    [GeneratedRegex(@"<[^>]+>", RegexOptions.NonBacktracking)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
