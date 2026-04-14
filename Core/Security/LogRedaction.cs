using System.Text.RegularExpressions;

namespace Sharpwire.Core.Security;

/// <summary>STANDARDS §3.2 — mask common secret patterns before UI / file logs.</summary>
public static partial class LogRedaction
{
    [GeneratedRegex(@"sk-[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex SkPattern();

    [GeneratedRegex(@"AIza[0-9A-Za-z\-_]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleApiKeyPattern();

    public static string MaskForUi(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        var s = SkPattern().Replace(text, "[REDACTED:api-key-pattern]");
        return GoogleApiKeyPattern().Replace(s, "[REDACTED:api-key-pattern]");
    }
}
