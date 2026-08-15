using System.Net;
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

        var text = ScriptAndStyle().Replace(html, " ");
        text = BlockTags().Replace(text, " ");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = Whitespace().Replace(text, " ").Trim();

        if (text.Length <= maxChars) return text;

        var cut = text.LastIndexOf(' ', Math.Min(maxChars, text.Length - 1));
        return (cut > 0 ? text[..cut] : text[..maxChars]).TrimEnd();
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptAndStyle();

    // Block elements become a space so words either side do not run together, while inline
    // elements are removed outright so punctuation stays attached to the word before it.
    [GeneratedRegex(@"</?(p|div|br|li|h[1-6]|tr|td|th|section|article|header|footer|blockquote|pre|ul|ol|table|dl|dt|dd|figure|figcaption|main|aside|hr)\b[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
