using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;

namespace Sharpwire.ViewModels;

public enum MessageRole
{
    User,
    System,
    Agent
}

public partial class ChatMessageViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private MessageRole _role;

    [ObservableProperty]
    private string _senderName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private string? _accentColorHex;

    [ObservableProperty]
    private DateTime _timestamp = DateTime.Now;

    public string FormattedTime => Timestamp.ToString("HH:mm:ss");

    public IBrush BackgroundBrush => Role switch
    {
        MessageRole.User => Brushes.DarkSlateBlue,
        MessageRole.System => Brushes.DarkSlateGray,
        // Same solid fill as the graph node header (AgentAccentOptions.ParseHeaderBrush).
        MessageRole.Agent => AgentAccentOptions.ParseHeaderBrush(AccentColorHex),
        _ => Brushes.Gray
    };

    public IBrush HeaderForegroundBrush => Role switch
    {
        MessageRole.User => Brushes.White,
        MessageRole.System => Brushes.Gainsboro,
        MessageRole.Agent => Brushes.White,
        _ => Brushes.LightGray
    };

    /// <summary>Body text inside the bubble (agent uses light text on saturated accent).</summary>
    public IBrush BodyForegroundBrush => Role switch
    {
        MessageRole.User => Brushes.White,
        MessageRole.System => Brushes.Gainsboro,
        MessageRole.Agent => Brushes.White,
        _ => Brushes.LightGray
    };

    public string HeaderText => Role switch
    {
        MessageRole.User => "You",
        MessageRole.System => "System",
        MessageRole.Agent => string.IsNullOrWhiteSpace(SenderName) ? "Agent" : SenderName,
        _ => "Unknown"
    };
}
