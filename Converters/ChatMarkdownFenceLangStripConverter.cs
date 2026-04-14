using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;

namespace Sharpwire.Converters;

// Display-only: SyntaxHigh CodePad clips fenced TextEditor when a language label is present; dropping the lang token avoids that.
public sealed class ChatMarkdownFenceLangStripConverter : IValueConverter
{
    private static readonly Regex FenceLine = new(
        @"^(?<ind>\s*)(?<fence>`{3,}|~{3,})(?<tail>[^\n\r]*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex LangOnly = new(
        @"^[\w.#+\-]+$",
        RegexOptions.Compiled);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || text.Length == 0)
            return value ?? string.Empty;

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

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
