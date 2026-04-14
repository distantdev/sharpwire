using Avalonia.Controls;
using Avalonia.Interactivity;
using Sharpwire.ViewModels;

namespace Sharpwire;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.PersistNonSecretSettings();
        };
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
