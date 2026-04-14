using System;
using System.Text;

namespace Sharpwire.Core;

/// <summary>
/// Models sometimes return “empty” replies using zero-width / format characters; those pass
/// <see cref="string.IsNullOrWhiteSpace"/> but render as blank (e.g. in Markdown viewers).
/// </summary>
public static class ChatTextNormalizer
{
    /// <summary>Strips common invisible characters, internal tags, and trims whitespace for chat display and empty checks.</summary>
    public static string ForDisplay(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\u200B' or '\u200C' or '\u200D' or '\uFEFF' or '\u2060' or '\u180E')
                continue;
            sb.Append(c);
        }

        var result = sb.Length == 0 ? string.Empty : TrimOuterWhitespace(sb.ToString());
        return result;
    }

    public static bool IsEffectivelyEmpty(string? text) => string.IsNullOrWhiteSpace(ForDisplay(text));

    private static string TrimOuterWhitespace(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && char.IsWhiteSpace(s[start]))
            start++;
        while (end > start && char.IsWhiteSpace(s[end - 1]))
            end--;
        return start == 0 && end == s.Length ? s : s[start..end];
    }
}
