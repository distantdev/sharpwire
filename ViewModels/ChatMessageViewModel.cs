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
    /// <summary>
    /// When true, the chat bubble feeds LiveMarkdown via append/replace events (streaming).
    /// When false, full body is synced from <see cref="Text"/>.
    /// </summary>
    [ObservableProperty]
    private bool _usesIncrementalMarkdown;

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

    /// <summary>Suffix to append to the LiveMarkdown builder (streaming incremental display).</summary>
    public event Action<string>? MarkdownAppendRequested;

    /// <summary>Replace entire markdown body when display diverges from prefix append (e.g. redaction).</summary>
    public event Action<string>? MarkdownReplaceRequested;

    internal void RaiseMarkdownAppend(string delta) => MarkdownAppendRequested?.Invoke(delta);

    internal void RaiseMarkdownReplace(string fullDisplay) => MarkdownReplaceRequested?.Invoke(fullDisplay);
}
