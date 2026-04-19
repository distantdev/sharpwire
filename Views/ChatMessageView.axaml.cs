using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LiveMarkdown.Avalonia;
using Sharpwire.Core;
using Sharpwire.ViewModels;

namespace Sharpwire.Views;

public partial class ChatMessageView : UserControl
{
    private readonly ObservableStringBuilder _markdownBuilder = new();
    private ChatMessageViewModel? _vm;

    public ChatMessageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (MarkdownBody != null)
            MarkdownBody.MarkdownBuilder = _markdownBuilder;
        HookViewModel();
    }

    private void HookViewModel()
    {
        UnhookViewModel();
        _markdownBuilder.Clear();

        _vm = DataContext as ChatMessageViewModel;
        if (_vm == null)
            return;

        _vm.MarkdownAppendRequested += OnMarkdownAppendRequested;
        _vm.MarkdownReplaceRequested += OnMarkdownReplaceRequested;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        SyncMarkdownFromViewModel();
    }

    private void UnhookViewModel()
    {
        if (_vm == null)
            return;

        _vm.MarkdownAppendRequested -= OnMarkdownAppendRequested;
        _vm.MarkdownReplaceRequested -= OnMarkdownReplaceRequested;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null ||
            e.PropertyName is nameof(ChatMessageViewModel.Text) or nameof(ChatMessageViewModel.UsesIncrementalMarkdown))
            SyncMarkdownFromViewModel();
    }

    /// <summary>Static bubbles (and catch-up): full replace from <see cref="ChatMessageViewModel.Text"/>.</summary>
    private void SyncMarkdownFromViewModel()
    {
        if (_vm == null || MarkdownBody == null)
            return;

        MarkdownBody.MarkdownBuilder = _markdownBuilder;

        if (_vm.UsesIncrementalMarkdown)
        {
            // View can attach after the first deferred pushes; replay from canonical Text once.
            if (_markdownBuilder.Length == 0 && _vm.Text.Length > 0)
            {
                var replay = ChatMarkdownFenceStrip.StripFenceLanguageLine(_vm.Text);
                if (replay.Length > 0)
                    _markdownBuilder.Append(replay);
            }

            return;
        }

        var md = ChatMarkdownFenceStrip.StripFenceLanguageLine(_vm.Text);
        _markdownBuilder.Clear();
        if (md.Length > 0)
            _markdownBuilder.Append(md);
    }

    private void OnMarkdownAppendRequested(string delta)
    {
        if (delta.Length > 0)
            _markdownBuilder.Append(delta);
    }

    private void OnMarkdownReplaceRequested(string fullMarkdown)
    {
        _markdownBuilder.Clear();
        if (fullMarkdown.Length > 0)
            _markdownBuilder.Append(fullMarkdown);
    }
}
