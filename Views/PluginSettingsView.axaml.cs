using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sharpwire.ViewModels;

namespace Sharpwire.Views;

public partial class PluginSettingsView : UserControl
{
    public PluginSettingsView()
    {
        InitializeComponent();
        AddHandler(InputElement.LostFocusEvent, OnDescendantLostFocus, RoutingStrategies.Bubble);
        DetachedFromVisualTree += (_, _) => FlushIfViewModel();
    }

    private void OnDescendantLostFocus(object? sender, RoutedEventArgs e)
    {
        FlushIfViewModel();
    }

    private void FlushIfViewModel()
    {
        if (DataContext is PluginSettingsViewModel vm)
            vm.FlushSave();
    }
}
