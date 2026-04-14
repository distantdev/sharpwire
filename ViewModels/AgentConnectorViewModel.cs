using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sharpwire.ViewModels;

public partial class AgentConnectorViewModel : ObservableObject
{
    public AgentNodeViewModel? Host { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Connector-specific tooltip (forward vs loop-back ports).</summary>
    public string PortToolTip { get; set; } = "Alt+click: disconnect";

    [ObservableProperty]
    private Point _anchor;

    [ObservableProperty]
    private bool _isConnected;
}
