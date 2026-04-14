using System;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Nodify;

namespace Sharpwire.ViewModels;

/// <summary>
/// Nodify edge for configured handoff routing only (NextAgentName / graph wiring).
/// Inter-agent dialogue is shown in chat (e.g. @mentions), not as graph connections.
/// </summary>
public partial class HandoffConnectionViewModel : ObservableObject
{
    /// <summary>How long the wire stays in flow highlight (stroke dash / opacity). ≥1s avoids a blink.</summary>
    private static readonly TimeSpan FlowPulseDuration = TimeSpan.FromSeconds(2);

    /// <summary>Graph pixels: one arrow; beyond this, add one per <see cref="PixelsPerExtraArrow"/>.</summary>
    private const double FirstArrowMaxLength = 72;

    private const double PixelsPerExtraArrow = 100;
    private const uint MinArrows = 1;
    private const uint MaxArrows = 7;

    private DispatcherTimer? _flowTimer;

    public AgentConnectorViewModel Source { get; }
    public AgentConnectorViewModel Target { get; }

    /// <summary>
    /// Nodify <see cref="CircuitConnection"/> endpoints for drawing.
    /// </summary>
    public AgentConnectorViewModel DirectionalSource => Source;

    /// <summary>Pair with <see cref="DirectionalSource"/>.</summary>
    public AgentConnectorViewModel DirectionalTarget => Target;

    /// <summary><see cref="ConnectionDirection.Forward"/> for default handoff; <see cref="ConnectionDirection.Backward"/> for return wires.</summary>
    public ConnectionDirection DirectionalFlow =>
        IsConditionalReturn ? ConnectionDirection.Backward : ConnectionDirection.Forward;

    /// <summary>True for the conditional wire: downstream <c>Return</c> output → upstream <c>Feedback</c> input.</summary>
    public bool IsConditionalReturn { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _edgeLabel;

    [ObservableProperty]
    private string? _conditionRef;

    public IBrush WireStrokeBrush { get; }

    public string ToolTipText =>
        string.IsNullOrWhiteSpace(EdgeLabel)
            ? "Right-click to edit condition — Alt+click: disconnect"
            : EdgeLabel + " — Right-click to edit condition — Alt+click: disconnect";

    [ObservableProperty]
    private bool _isFlowPulse;

    /// <summary>Maps to <see cref="Nodify.CircuitConnection.DirectionalArrowsCount"/> (evenly spaced along the segment).</summary>
    [ObservableProperty]
    private uint _arrowCount = 1;

    public HandoffConnectionViewModel(
        AgentConnectorViewModel source,
        AgentConnectorViewModel target,
        bool isConditionalReturn = false,
        string? edgeLabel = null,
        string? conditionRef = null)
    {
        Source = source;
        Target = target;
        IsConditionalReturn = isConditionalReturn;
        _edgeLabel = edgeLabel;
        _conditionRef = conditionRef;
        WireStrokeBrush = new SolidColorBrush(
            isConditionalReturn ? Color.Parse("#E86B55") : Color.Parse("#5AA0C8DD"));
        source.PropertyChanged += OnConnectorPropertyChanged;
        target.PropertyChanged += OnConnectorPropertyChanged;
        source.IsConnected = true;
        target.IsConnected = true;
        UpdateArrowCount();
    }

    private void OnConnectorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AgentConnectorViewModel.Anchor))
            return;
        UpdateArrowCount();
        OnPropertyChanged(nameof(DirectionalSource));
        OnPropertyChanged(nameof(DirectionalTarget));
    }

    private void UpdateArrowCount()
    {
        var dx = Target.Anchor.X - Source.Anchor.X;
        var dy = Target.Anchor.Y - Source.Anchor.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var n = 1 + (int)Math.Max(0, (len - FirstArrowMaxLength) / PixelsPerExtraArrow);
        var clamped = (uint)Math.Clamp(n, (int)MinArrows, (int)MaxArrows);
        if (ArrowCount != clamped)
            ArrowCount = clamped;
    }

    /// <summary>Highlight this edge only for an explicit runtime handoff step in the agent chain (not chat). Restarts the 1s hold if called again while active.</summary>
    public void PulseFlow()
    {
        IsFlowPulse = true;
        _flowTimer ??= new DispatcherTimer { Interval = FlowPulseDuration };
        _flowTimer.Stop();
        _flowTimer.Tick -= OnFlowTimerTick;
        _flowTimer.Tick += OnFlowTimerTick;
        _flowTimer.Start();
    }

    private void OnFlowTimerTick(object? sender, EventArgs e)
    {
        if (_flowTimer != null)
        {
            _flowTimer.Stop();
            _flowTimer.Tick -= OnFlowTimerTick;
        }

        IsFlowPulse = false;
    }

    public void Detach()
    {
        Source.PropertyChanged -= OnConnectorPropertyChanged;
        Target.PropertyChanged -= OnConnectorPropertyChanged;
        _flowTimer?.Stop();
        _flowTimer = null;
        Source.IsConnected = false;
        Target.IsConnected = false;
    }
}
