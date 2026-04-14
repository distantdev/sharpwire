using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using Sharpwire.ViewModels;

namespace Sharpwire.Views;

public partial class ChatMessageView : UserControl
{
    private static readonly SolidColorBrush ChatCodeForeground = new(Color.Parse("#F0F6FC"));
    private static readonly SolidColorBrush ChatCodeBackground = new(Color.Parse("#0D1117"));

    private ChatMessageViewModel? _vm;
    private bool _fixingEditors;
    private long _lastLayoutFixTicks;
    private DispatcherTimer? _deferredEditorFix;

    public ChatMessageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _deferredEditorFix?.Stop();
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (MarkdownBody == null)
            return;
        MarkdownBody.LayoutUpdated -= OnMarkdownLayoutUpdated;
        MarkdownBody.LayoutUpdated += OnMarkdownLayoutUpdated;
        MarkdownBody.PropertyChanged -= OnMarkdownBodyPropertyChanged;
        MarkdownBody.PropertyChanged += OnMarkdownBodyPropertyChanged;
        ScheduleFixFencedCodeEditors();
    }

    private void OnMarkdownBodyPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property?.Name is "Markdown")
            ScheduleFixFencedCodeEditors();
    }

    private void OnMarkdownLayoutUpdated(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        if (now - _lastLayoutFixTicks < 80)
            return;
        _lastLayoutFixTicks = now;
        ScheduleFixFencedCodeEditors();
    }

    private void HookViewModel()
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as ChatMessageViewModel;
        if (_vm != null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        ScheduleFixFencedCodeEditors();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(ChatMessageViewModel.Text))
            ScheduleFixFencedCodeEditors();
    }

    private void ScheduleFixFencedCodeEditors()
    {
        Dispatcher.UIThread.Post(FixFencedCodeEditors, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(FixFencedCodeEditors, DispatcherPriority.Render);

        _deferredEditorFix ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _deferredEditorFix.Tick -= OnDeferredEditorFixTick;
        _deferredEditorFix.Tick += OnDeferredEditorFixTick;
        _deferredEditorFix.Stop();
        _deferredEditorFix.Start();
    }

    private void OnDeferredEditorFixTick(object? sender, EventArgs e)
    {
        _deferredEditorFix?.Stop();
        FixFencedCodeEditors();
    }

    private void FixFencedCodeEditors()
    {
        if (_fixingEditors || MarkdownBody == null)
            return;
        _fixingEditors = true;
        try
        {
            foreach (var ed in MarkdownBody.GetVisualDescendants().OfType<TextEditor>())
            {
                ed.SyntaxHighlighting = null;

                var tv = ed.TextArea.TextView;
                var list = tv.LineTransformers;
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] is HighlightingColorizer or RichTextColorizer)
                        list.RemoveAt(i);
                }

                ed.Foreground = ChatCodeForeground;
                ed.Background = ChatCodeBackground;
                ed.LineNumbersForeground = ChatCodeForeground;

                tv.NonPrintableCharacterBrush = ChatCodeForeground;
                tv.CurrentLineBackground = Brushes.Transparent;

                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] is ChatMarkdownCodeForegroundTransformer)
                        list.RemoveAt(i);
                }

                list.Add(new ChatMarkdownCodeForegroundTransformer(ChatCodeForeground));
                tv.Redraw();
            }
        }
        finally
        {
            _fixingEditors = false;
        }
    }

    private sealed class ChatMarkdownCodeForegroundTransformer : DocumentColorizingTransformer
    {
        private readonly IBrush _foreground;

        public ChatMarkdownCodeForegroundTransformer(IBrush foreground) =>
            _foreground = foreground;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0)
                return;
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                var p = element.TextRunProperties;
                p.SetForegroundBrush(_foreground);
                var tf = p.Typeface;
                p.SetTypeface(new Typeface(tf.FontFamily, FontStyle.Normal, FontWeight.Normal, tf.Stretch));
            });
        }
    }
}
