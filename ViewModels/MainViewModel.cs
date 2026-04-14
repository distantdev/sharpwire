using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sharpwire.ViewModels;

public class NegateConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly NegateConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) 
    {
        if (value is double d)
        {
            // Round to nearest pixel to prevent "skipped" rows/columns during sub-pixel panning
            return -Math.Round(d);
        }
        return 0.0;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class ZoomOpacityConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is double zoom)
        {
            // Fully visible at 1.0, disappears below 0.4
            if (zoom >= 0.6) return 1.0;
            if (zoom <= 0.3) return 0.0;
            return (zoom - 0.3) / 0.3; // Simple linear fade
        }
        return 1.0;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class OpacityConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly OpacityConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is bool b && b ? 1.0 : 0.6;
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public sealed class AccentPresetMatchToBrushConverter : IMultiValueConverter
{
    public static readonly AccentPresetMatchToBrushConverter Instance = new();
    private static readonly IBrush SelectedRing = new SolidColorBrush(Color.Parse("#c8c8c8"));

    public object? Convert(IList<object?> values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not AccentPreset selected || values[1] is not AccentPreset item)
            return Brushes.Transparent;
        return selected == item ? SelectedRing : Brushes.Transparent;
    }
}

public partial class ProviderStatusViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _status = "OFFLINE";
}

public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<BaseNodeViewModel> Nodes { get; } = new();

    public ObservableCollection<HandoffConnectionViewModel> Connections { get; } = new();

    public WorkspaceViewModel Workspace { get; }
    public ChatViewModel Chat { get; }

    public ObservableCollection<TabItemViewModel> Tabs { get; } = new();

    public ObservableCollection<ProviderStatusViewModel> ProviderStatuses { get; } = new();

    [ObservableProperty]
    private bool _isPluginCompiling;

    [ObservableProperty]
    private string? _pluginStatusText;

    [ObservableProperty]
    private string? _pluginErrorMessage;

    [ObservableProperty]
    private IBrush _pluginStatusColor = Brushes.Gray;

    [ObservableProperty]
    private TabItemViewModel? _selectedTab;

    [ObservableProperty]
    private bool _isWorkspaceVisible = true;

    [RelayCommand]
    private void ToggleWorkspace() => IsWorkspaceVisible = !IsWorkspaceVisible;

    public MainViewModel(WorkspaceViewModel workspace, ChatViewModel chat)
    {
        Workspace = workspace;
        Chat = chat;

        // Add the permanent Graph tab
        var graphTab = new TabItemViewModel 
        { 
            Header = "THE GRID", 
            Content = new GraphTabViewModel(),
            IsClosable = false 
        };
        Tabs.Add(graphTab);

        // Add the permanent Orchestrator tab (content will be set by MainWindow once agents are ready)
        var orchestratorTab = new TabItemViewModel
        {
            Header = "ORCHESTRATOR",
            IsClosable = false
        };
        Tabs.Add(orchestratorTab);

        SelectedTab = graphTab;
    }
}
