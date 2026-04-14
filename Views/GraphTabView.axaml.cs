using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Nodify;
using Nodify.Compatibility;
using Sharpwire.ViewModels;
using Sharpwire;

namespace Sharpwire.Views;

public partial class GraphTabView : UserControl
{
    private NodifyEditor? _editor;

    private EventHandler<PointerPressedEventArgs>? _ptrPressed;

    public GraphTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _editor = Editor;
        if (_editor == null)
            return;

        ApplyGridPanInsteadOfMarqueeSelection();

        _ptrPressed = OnEditorPointerPressed;
        _editor.AddHandler(InputElement.PointerPressedEvent, _ptrPressed, RoutingStrategies.Tunnel);

        if (TopLevel.GetTopLevel(this) is MainWindow mw)
            mw.AttachGraphEditor(_editor);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_editor != null && _ptrPressed != null)
            _editor.RemoveHandler(InputElement.PointerPressedEvent, _ptrPressed);

        _editor = null;
        _ptrPressed = null;
    }

    /// <summary>Pan the graph with left-drag on empty canvas instead of marquee selection.</summary>
    private static void ApplyGridPanInsteadOfMarqueeSelection()
    {
        var editor = EditorGestures.Mappings.Editor;
        editor.Pan.Value = new AnyGesture(
            new MouseGesture(MouseAction.LeftClick),
            new MouseGesture(MouseAction.RightClick),
            new MouseGesture(MouseAction.MiddleClick));
        editor.Selection.Apply(EditorGestures.SelectionGestures.None);
    }

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual host)
            return;
        if (!e.GetCurrentPoint(host).Properties.IsLeftButtonPressed)
            return;

        if (IsUnderConnector(e.Source as Visual))
            return;

        var node = FindAgentNodeFromSource(e.Source as Visual);
        if (node == null)
            return;

        if (e.ClickCount < 2)
            return;

        if (TopLevel.GetTopLevel(this) is MainWindow mw)
            mw.OpenAgentEditorFromGraph(node.AgentKey);
    }

    private static AgentNodeViewModel? FindAgentNodeFromSource(Visual? v)
    {
        while (v != null)
        {
            if (v is Control c && c.DataContext is AgentNodeViewModel an)
                return an;
            v = v.GetVisualParent();
        }

        return null;
    }

    private static bool IsUnderConnector(Visual? v)
    {
        while (v != null)
        {
            if (v is Connector)
                return true;
            v = v.GetVisualParent();
        }

        return false;
    }
}
