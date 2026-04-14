using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sharpwire.Core;
using Sharpwire.Core.Security;

namespace Sharpwire.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isThinking;

    [ObservableProperty]
    private string _thinkingAgentName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThinkingAgentNameBrush))]
    private string? _thinkingAgentAccentHex;

    public IBrush ThinkingAgentNameBrush => AgentAccentOptions.ParseHeaderBrush(ThinkingAgentAccentHex);

    public event Action<string>? OnMessageSent;

    /// <summary>Raised before the user's outgoing message is appended (so UI can pin auto-scroll).</summary>
    public event Action? BeforeUserMessageAppended;

    public event Action? StreamingChatUpdated;

    private string? _inflightStreamSender;
    private string? _inflightStreamAccent;
    private string _inflightRaw = string.Empty;
    private ChatMessageViewModel? _inflightChatRow;

    public void AddMessage(MessageRole role, string text, string senderName = "", string? accentColorHex = null)
    {
        var body = ChatTextNormalizer.ForDisplay(LogRedaction.MaskForUi(text));
        if (role == MessageRole.Agent && string.IsNullOrWhiteSpace(body))
        {
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_inflightStreamSender != null &&
                    string.Equals(senderName, _inflightStreamSender, StringComparison.OrdinalIgnoreCase))
                {
                    if (_inflightChatRow != null)
                        Messages.Remove(_inflightChatRow);
                    ClearInflightStreamState();
                }
            });
            return;
        }

        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (role == MessageRole.Agent &&
                _inflightStreamSender != null &&
                string.Equals(senderName, _inflightStreamSender, StringComparison.OrdinalIgnoreCase))
            {
                if (_inflightChatRow != null)
                    _inflightChatRow.Text = body;
                else
                {
                    Messages.Add(new ChatMessageViewModel
                    {
                        Role = role,
                        Text = body,
                        SenderName = senderName,
                        AccentColorHex = accentColorHex ?? _inflightStreamAccent
                    });
                }

                ClearInflightStreamState();
                return;
            }

            Messages.Add(new ChatMessageViewModel
            {
                Role = role,
                Text = body,
                SenderName = senderName,
                AccentColorHex = accentColorHex
            });
        });
    }

    public void BeginAgentModelStream(string agentName, string? accentColorHex)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            _inflightStreamSender = agentName;
            _inflightStreamAccent = accentColorHex;
            _inflightRaw = string.Empty;
            _inflightChatRow = null;
        });
    }

    public void AppendAgentModelStreamDelta(string agentName, string? delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;

        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_inflightStreamSender == null ||
                !string.Equals(agentName, _inflightStreamSender, StringComparison.OrdinalIgnoreCase))
                return;

            _inflightRaw += delta;
            var display = ChatTextNormalizer.ForDisplay(LogRedaction.MaskForUi(_inflightRaw));
            if (string.IsNullOrWhiteSpace(display))
                return;

            if (_inflightChatRow == null)
            {
                _inflightChatRow = new ChatMessageViewModel
                {
                    Role = MessageRole.Agent,
                    Text = display,
                    SenderName = agentName,
                    AccentColorHex = _inflightStreamAccent
                };
                Messages.Add(_inflightChatRow);
            }
            else
                _inflightChatRow.Text = display;

            StreamingChatUpdated?.Invoke();
        });
    }

    public void EndAgentModelStream(string agentName)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_inflightStreamSender == null ||
                !string.Equals(agentName, _inflightStreamSender, StringComparison.OrdinalIgnoreCase))
                return;

            if (_inflightChatRow != null && string.IsNullOrWhiteSpace(_inflightChatRow.Text))
            {
                Messages.Remove(_inflightChatRow);
                _inflightChatRow = null;
                ClearInflightStreamState();
                return;
            }

            if (_inflightChatRow == null && string.IsNullOrEmpty(_inflightRaw))
                ClearInflightStreamState();
        });
    }

    private void ClearInflightStreamState()
    {
        _inflightStreamSender = null;
        _inflightStreamAccent = null;
        _inflightRaw = string.Empty;
        _inflightChatRow = null;
    }

    public void ClearChat()
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClearInflightStreamState();
            Messages.Clear();
        });
    }

    [RelayCommand]
    private void SendMessage()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;

        var message = InputText;
        BeforeUserMessageAppended?.Invoke();
        AddMessage(MessageRole.User, message, "You");
        InputText = string.Empty;

        OnMessageSent?.Invoke(message);
    }
}
