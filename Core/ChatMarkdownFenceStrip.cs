using System.Text.RegularExpressions;

namespace Sharpwire.Core;

/// <summary>
/// Display-only: some markdown renderers clip fenced code headers when a language token is present.
/// </summary>
public static class ChatMarkdownFenceStrip
{
    private static readonly Regex FenceLine = new(
        @"^(?<ind>\s*)(?<fence>`{3,}|~{3,})(?<tail>[^\n\r]*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex LangOnly = new(
        @"^[\w.#+\-]+$",
        RegexOptions.Compiled);

    public static string StripFenceLanguageLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        return FenceLine.Replace(text, m =>
        {
            var tail = m.Groups["tail"].Value.Trim();
            if (tail.Length == 0 || !LangOnly.IsMatch(tail))
                return m.Value;

            var ind = m.Groups["ind"].Value;
            var fence = m.Groups["fence"].Value;
            return $"{ind}{fence}";
        });
    }
}
