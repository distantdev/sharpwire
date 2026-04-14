using System.Collections.Generic;
using Avalonia.Media;

namespace Sharpwire.ViewModels;

public record AccentPreset(string Label, string Hex)
{
    public IBrush SwatchBrush => AgentAccentOptions.ParseHeaderBrush(Hex);
}

/// <summary>Named colors for agent chat bubbles and graph node headers.</summary>
public static class AgentAccentOptions
{
    public static IReadOnlyList<AccentPreset> Presets { get; } =
    [
        new("Teal", "#0D7377"),
        new("Purple", "#7B4397"),
        new("Blue", "#3D6B9A"),
        new("Green", "#2E7D32"),
        new("Amber", "#C77D0A"),
        new("Coral", "#C45C4A"),
        new("Pink", "#B84D7A"),
        new("Slate", "#5C6B7A")
    ];

    public static IBrush ParseHeaderBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex.Trim(), out var c))
            c = Color.FromRgb(13, 115, 119);
        return new SolidColorBrush(c);
    }
}
