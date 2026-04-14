using Avalonia.Controls;
using Avalonia.Input;
using Sharpwire.ViewModels;

namespace Sharpwire.Views;

public partial class AccentSwatchPicker : UserControl
{
    public AccentSwatchPicker() => InitializeComponent();

    private void Swatch_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not AccentPreset preset)
            return;
        switch (DataContext)
        {
            case AgentEditorViewModel a:
                a.SelectedAccentPreset = preset;
                break;
            case OrchestratorEditorViewModel o:
                o.SelectedAccentPreset = preset;
                break;
        }
    }
}
