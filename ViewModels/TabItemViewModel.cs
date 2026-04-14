using CommunityToolkit.Mvvm.ComponentModel;

namespace Sharpwire.ViewModels;

public partial class TabItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private object? _content;

    [ObservableProperty]
    private bool _isClosable = true;
}

public class GraphTabViewModel : ObservableObject { }
