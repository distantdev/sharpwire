using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia;

namespace Sharpwire.ViewModels;

public partial class BaseNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Node";

    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private bool _isSelected;
}
