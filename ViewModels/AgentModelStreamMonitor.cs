using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sharpwire.ViewModels;

/// <summary>Session-scoped buffer for streamed model text per agent (survives closed editor tabs).</summary>
public partial class AgentModelStreamMonitor : ObservableObject
{
    [ObservableProperty]
    private string _sessionLog = string.Empty;

    [ObservableProperty]
    private bool _isReceivingStream;

    public void BeginStream()
    {
        IsReceivingStream = true;
        if (SessionLog.Length > 0)
            SessionLog += "\n\n";
        SessionLog += $"── {DateTime.Now:yyyy-MM-dd HH:mm:ss} ──\n\n";
    }

    public void AppendDelta(string? delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;
        SessionLog += delta;
    }

    public void EndStream() => IsReceivingStream = false;

    [RelayCommand]
    private void ClearOutput() => SessionLog = string.Empty;
}
