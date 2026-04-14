using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sharpwire.ViewModels;

public enum AgentState
{
    Idle,
    Busy,
    Error,
    Success
}

public partial class AgentNodeViewModel : BaseNodeViewModel
{
    /// <summary>Canonical agent name matching persisted agent definitions (layout / session keys).</summary>
    public string AgentKey { get; set; } = string.Empty;

    public ObservableCollection<AgentConnectorViewModel> Input { get; } = new();
    public ObservableCollection<AgentConnectorViewModel> Output { get; } = new();

    private static readonly TimeSpan CommPulseDuration = TimeSpan.FromSeconds(1);

    private DispatcherTimer? _commTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdleLedOpacity))]
    [NotifyPropertyChangedFor(nameof(IsWorkingOutline))]
    [NotifyPropertyChangedFor(nameof(IdleBreathing))]
    private AgentState _state = AgentState.Idle;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorLedOpacity))]
    [NotifyPropertyChangedFor(nameof(IdleBreathing))]
    private bool _hasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkingOutline))]
    private bool _thinkingPulse;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkingOutline))]
    private bool _toolPulse;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkingOutline))]
    private bool _commPulse;

    [ObservableProperty]
    private IBrush _headerBrush = Brushes.DarkCyan;

    public double IdleLedOpacity => State switch
    {
        AgentState.Error => 0.12,
        AgentState.Busy => 0.38,
        _ => 1.0
    };

    public double ErrorLedOpacity => HasError ? 1.0 : 0.11;

    /// <summary>True while the agent is busy, thinking, using tools, or showing a comm pulse (graph outline glow).</summary>
    public bool IsWorkingOutline => State == AgentState.Busy || ThinkingPulse || ToolPulse || CommPulse;

    /// <summary>Ready / standby — gentle breathe on the power icon (idle, success, any non-error resting state).</summary>
    public bool IdleBreathing => !HasError && State is AgentState.Idle or AgentState.Success;

    partial void OnStateChanged(AgentState value)
    {
        IsActive = value == AgentState.Busy;
        HasError = value == AgentState.Error;
        if (value == AgentState.Error)
        {
            ThinkingPulse = false;
            ToolPulse = false;
            CommPulse = false;
            _commTimer?.Stop();
        }
    }

    /// <summary>Updates think/tool LEDs from the agent service. Comm is driven by <see cref="PulseCommunicating"/>.</summary>
    public void ApplyActivity(AgentActivityKind activity)
    {
        ThinkingPulse = activity == AgentActivityKind.Thinking;
        ToolPulse = activity == AgentActivityKind.ToolUse;
    }

    /// <summary>Briefly pulses the message / link LED (chat or internal handoff traffic).</summary>
    public void PulseCommunicating()
    {
        if (State == AgentState.Error)
            return;

        CommPulse = true;
        _commTimer ??= new DispatcherTimer { Interval = CommPulseDuration };
        _commTimer.Stop();
        _commTimer.Tick -= OnCommTimerTick;
        _commTimer.Tick += OnCommTimerTick;
        _commTimer.Start();
    }

    private void OnCommTimerTick(object? sender, EventArgs e)
    {
        if (_commTimer != null)
        {
            _commTimer.Stop();
            _commTimer.Tick -= OnCommTimerTick;
        }

        CommPulse = false;
    }
}
