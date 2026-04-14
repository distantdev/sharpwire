using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nodify;
using Sharpwire.Core.Agents;
using Sharpwire.Core;
using Sharpwire.Core.MetaToolbox;
using Sharpwire.Core.Secrets;
using Sharpwire.Core.Session;
using Sharpwire.Core.Tools;
using Sharpwire.Core.Update;
using Sharpwire.Core.Workflow;
using Sharpwire.ViewModels;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;

namespace Sharpwire;

public partial class MainWindow : Window
{
    private const double AgentNodeWidth = 240;
    private const double AgentNodeHeight = 120;
    private const double GridSnap = 30;

    /// <summary>Minimum clear gap between node bounding boxes (one grid cell), same as <see cref="GridSnap"/>.</summary>
    private static double NodeLayoutGutter => GridSnap;

    private bool _applyingNodeLayoutCorrection;

    private readonly AgentService _agentService;
    private readonly GitBackstop _gitBackstop;
    private readonly PluginCompilerService _pluginCompiler;
    private readonly SettingsManager _settingsManager;
    private readonly LlmApiKeyStore _apiKeyStore;
    private readonly AgentSession _session;
    private readonly ToolApprovalCoordinator _toolApproval;
    private readonly IAppUpdateService _appUpdateService;

    private readonly string _workspacePath;
    private CancellationTokenSource? _cts;
    private bool _panicActive;

    private readonly MainViewModel _viewModel;
    private readonly WorkspaceViewModel _workspaceViewModel;
    private readonly ChatViewModel _chatViewModel;
    private OrchestratorEditorViewModel? _orchestratorEditorVm;

    private bool _chatFollowTail = true;
    private int _chatMessagesAddedWhileScrolledUp;
    private const double ChatScrollBottomThreshold = 48;
    private readonly HashSet<string> _busyAgents = new(StringComparer.OrdinalIgnoreCase);

    public string? InitialPrompt { get; set; }

    /// <summary>Required by Avalonia resource loader / previewer.</summary>
#pragma warning disable CS8618 // Designer-only; production uses DI constructor.
    public MainWindow()
    {
        InitializeComponent();
    }
#pragma warning restore CS8618

    public MainWindow(
        AppPaths paths,
        AgentService agentService,
        SettingsManager settingsManager,
        LlmApiKeyStore apiKeyStore,
        AgentSession agentSession,
        ToolApprovalCoordinator toolApproval,
        GitBackstop gitBackstop,
        PluginCompilerService pluginCompiler,
        IAppUpdateService appUpdateService) : this()
    {
        WorkspaceTree.AddHandler(TreeViewItem.ExpandedEvent, OnWorkspaceTreeItemExpanded, RoutingStrategies.Bubble);
        WorkspaceTree.AddHandler(TreeViewItem.CollapsedEvent, OnWorkspaceTreeItemCollapsed, RoutingStrategies.Bubble);

        _workspacePath = paths.WorkspaceDirectory;
        _settingsManager = settingsManager;
        _apiKeyStore = apiKeyStore;
        _session = agentSession;
        _toolApproval = toolApproval;
        _appUpdateService = appUpdateService;
        _toolApproval.TryRequestApprovalAsync = async (toolId, details) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                return await ShowWriteApprovalDialogAsync(toolId, details).ConfigureAwait(true);
            return await Dispatcher.UIThread.InvokeAsync(async () => await ShowWriteApprovalDialogAsync(toolId, details)).ConfigureAwait(true);
        };
        _agentService = agentService;
        _gitBackstop = gitBackstop;
        _gitBackstop.Initialize();
        _pluginCompiler = pluginCompiler;
        
        _workspaceViewModel = new WorkspaceViewModel(_workspacePath, ConfirmPurgeArtifactAsync);
        _chatViewModel = new ChatViewModel();
        
        _viewModel = new MainViewModel(_workspaceViewModel, _chatViewModel);
        DataContext = _viewModel;
        _workspaceViewModel.LoadRootItems();

        // Wire up chat event
        _chatViewModel.OnMessageSent += async (msg) => await RunTask(msg);
        
        // Wire up agent logs
        var logLock = new object();
        _agentService.OnLog += (msg) => {
            lock (logLock)
            {
                try 
                {
                    var path = Path.Combine(_workspacePath, ".sharpwire", "system.log");
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var sw = new StreamWriter(fs);
                    sw.Write($"{DateTime.Now:HH:mm:ss} | {msg}\n");
                }
                catch { /* Ignore log write failures */ }
            }
        };
        _agentService.OnChatMessage += (role, msg, sender, accent) =>
        {
            _chatViewModel.AddMessage(role, msg, sender, accent);
            if (role == MessageRole.Agent && !string.IsNullOrEmpty(sender))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (TryGetAgentNode(sender) is { } node)
                        node.PulseCommunicating();
                });
            }
        };
        _agentService.OnAgentHandoffFlow += (from, to, isReturn) =>
        {
            Dispatcher.UIThread.Post(() => PulseHandoffConnection(from, to, isReturn));
        };
        _agentService.OnAgentRenamed += AgentService_OnAgentRenamed;
        _agentService.OnOrchestratorPromptUpdated += AgentService_OnOrchestratorPromptUpdated;
        _agentService.OnHandoffTopologyChanged += () =>
        {
            // Always Post to ensure it runs AFTER any pending OnAgentAdded calls in the UI queue.
            Dispatcher.UIThread.Post(RebuildHandoffConnections);
        };
        _agentService.OnSceneLoaded += () =>
        {
            Dispatcher.UIThread.Post(() => {
                // Clear state that OnAgentsReady won't handle
                var tabsToRemove = _viewModel.Tabs.Where(t => t.Content is AgentEditorViewModel || t.Content is AgentLogViewModel).ToList();
                foreach (var t in tabsToRemove)
                    _viewModel.Tabs.Remove(t);
            });
        };

        _chatViewModel.Messages.CollectionChanged += ChatMessages_CollectionChanged;
        _chatViewModel.PropertyChanged += ChatViewModel_PropertyChanged;
        _chatViewModel.StreamingChatUpdated += ScrollChatToEndIfFollowing;
        _chatViewModel.BeforeUserMessageAppended += () =>
        {
            _chatFollowTail = true;
            _chatMessagesAddedWhileScrolledUp = 0;
            UpdateJumpToLatestChatUi();
        };
        ChatScrollViewer.ScrollChanged += ChatScrollViewer_ScrollChanged;
        _agentService.OnAgentAppearanceChanged += name =>
            Dispatcher.UIThread.Post(() => SyncAgentNodeHeader(name));
        
        // Dynamic node handling
        _agentService.OnAgentAdded += (agent) => {
            Dispatcher.UIThread.Post(() => {
                var indexBefore = _viewModel.Nodes.Count;
                var editor = TryFindGraphNodifyEditor();
                Point? centered = editor != null ? TryGetViewportCenterPlacement(editor) : null;
                var loc = ResolveNodeLocation(agent.Definition.Name, indexBefore, centered);

                var node = new AgentNodeViewModel
                {
                    AgentKey = agent.Definition.Name,
                    Title = agent.Definition.Name.ToUpper(),
                    Location = loc,
                    HeaderBrush = AgentAccentOptions.ParseHeaderBrush(agent.Definition.AccentColor)
                };
                WireAgentConnectors(node, agent.Definition);
                WireNodeLayoutPersistence(node);
                _viewModel.Nodes.Add(node);

                // Auto-pan to the new node
                if (editor != null)
                {
                    TryBringAgentNodeIntoViewCenter(editor, node.Title);
                }
            });
        };

        // Initialize the Nodes and Orchestrator Tab
        _agentService.OnAgentsReady += () => {
            Dispatcher.UIThread.Post(() => {
                _viewModel.Nodes.Clear();
                _viewModel.Connections.Clear();
                var allToolIds = _agentService.AvailableTools.Keys.ToList();

                foreach (var tab in _viewModel.Tabs)
                {
                    if (tab.Content is AgentEditorViewModel evm)
                    {
                        if (_agentService.Agents.TryGetValue(evm.PersistenceKey, out var ag))
                            evm.RefreshTools(allToolIds, ag.Definition.EnabledTools);
                    }
                    else if (tab.Content is OrchestratorEditorViewModel ovm)
                    {
                        if (_agentService.Agents.TryGetValue(AgentService.OrchestratorAgentName, out var orch))
                            ovm.RefreshTools(allToolIds, orch.Definition.EnabledTools);
                    }
                }

                int count = 0;
                foreach (var agent in _agentService.Agents.Values)
                {
                    if (AgentService.IsHiddenSystemAgent(agent.Definition.Name))
                        continue;

                    if (agent.Definition.Name == AgentService.OrchestratorAgentName)
                    {
                        // Set up the permanent Orchestrator tab
                        if (_viewModel.Tabs[1].Content is not OrchestratorEditorViewModel)
                        {
                            var orchVM = new OrchestratorEditorViewModel(agent.Definition, allToolIds);

                            orchVM.OnChanged += (newDef) => {
                                _agentService.UpdateAgent(AgentService.OrchestratorAgentName, newDef);
                            };
                            orchVM.OnAccentPersistRequested += (n, h) => _agentService.SetAgentAccent(n, h);

                            orchVM.OnDuplicateRequested += (draft) => FinalizeAndAddDuplicate(draft, AgentService.OrchestratorAgentName);
                            orchVM.ConfirmDuplicateAsync = () => ConfirmDuplicateAgentAsync(AgentService.OrchestratorAgentName);
                            orchVM.OnOpenSessionLogRequested += OpenAgentSessionLogTab;

                            _viewModel.Tabs[1].Content = orchVM;
                            _orchestratorEditorVm = orchVM;
                        }
                        
                        if (_agentService.Agents.TryGetValue(AgentService.OrchestratorAgentName, out var orchAgent)
                            && !string.IsNullOrEmpty(orchAgent.DynamicInstructions))
                            _orchestratorEditorVm?.ApplyRuntimeSystemPrompt(orchAgent.DynamicInstructions);
                        
                        continue;
                    }

                    var loc = ResolveNodeLocation(agent.Definition.Name, count, null);

                    var node = new AgentNodeViewModel
                    {
                        AgentKey = agent.Definition.Name,
                        Title = agent.Definition.Name.ToUpper(),
                        Location = loc,
                        HeaderBrush = AgentAccentOptions.ParseHeaderBrush(agent.Definition.AccentColor)
                    };
                    WireAgentConnectors(node, agent.Definition);
                    WireNodeLayoutPersistence(node);
                    _viewModel.Nodes.Add(node);
                    count++;
                }

                RebuildHandoffConnections();

                if (!string.IsNullOrWhiteSpace(InitialPrompt))
                {
                    var task = InitialPrompt;
                    InitialPrompt = null;
                    _chatViewModel.InputText = task;
                    _agentService.Log($"System: Launching with initial prompt: {task}");
                    Task.Run(async () => await RunTask(task));
                }
            });
        };

        // Wire up agent state changes
        _agentService.OnAgentStateChanged += (name, state) => {
            Dispatcher.UIThread.Post(() => {
                if (TryGetAgentNode(name) is { } node)
                    node.State = state;

                if (state == AgentState.Busy)
                    _busyAgents.Add(name);
                else
                    _busyAgents.Remove(name);

                SyncThinkingIndicator();
            });
        };

        _agentService.OnAgentActivityChanged += (name, activity) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (TryGetAgentNode(name) is { } node)
                    node.ApplyActivity(activity);
            });
        };

        _agentService.OnAgentModelStreamStarted += (name, accentHex) =>
            Dispatcher.UIThread.Post(() =>
            {
                _agentService.GetOrCreateModelStreamMonitor(name).BeginStream();
                _chatViewModel.BeginAgentModelStream(name, accentHex);
            });
        _agentService.OnAgentModelStreamDelta += (name, delta) =>
            Dispatcher.UIThread.Post(() =>
            {
                _agentService.GetOrCreateModelStreamMonitor(name).AppendDelta(delta);
                _chatViewModel.AppendAgentModelStreamDelta(name, delta);
            });
        _agentService.OnAgentModelStreamEnded += name =>
            Dispatcher.UIThread.Post(() =>
            {
                _agentService.GetOrCreateModelStreamMonitor(name).EndStream();
                _chatViewModel.EndAgentModelStream(name);
            });

        _agentService.OnProviderStatusChanged += (name, isConnected, status) =>
            Dispatcher.UIThread.Post(() => {
                var existing = _viewModel.ProviderStatuses.FirstOrDefault(p => p.Name == name);
                if (existing == null)
                {
                    _viewModel.ProviderStatuses.Add(new ProviderStatusViewModel { Name = name, IsConnected = isConnected, Status = status });
                }
                else
                {
                    existing.IsConnected = isConnected;
                    existing.Status = status;
                }
            });

        _agentService.OnPluginStatusUpdated += (isCompiling, error) =>
            Dispatcher.UIThread.Post(() => {
                _viewModel.IsPluginCompiling = isCompiling;
                if (isCompiling)
                {
                    _viewModel.PluginStatusText = "COMPILING PLUGINS...";
                    _viewModel.PluginErrorMessage = "Compilation in progress...";
                    _viewModel.PluginStatusColor = Brushes.Orange;
                }
                else if (error != null)
                {
                    _viewModel.PluginStatusText = "PLUGIN ERROR";
                    _viewModel.PluginErrorMessage = error;
                    _viewModel.PluginStatusColor = Brushes.Red;
                    _chatViewModel.AddMessage(MessageRole.System, $"Plugin Compilation Error: {error}", "System");
                }
                else
                {
                    _viewModel.PluginStatusText = "PLUGINS READY";
                    _viewModel.PluginErrorMessage = "All plugins compiled successfully.";
                    _viewModel.PluginStatusColor = Brushes.SpringGreen;
                }
            });
        
        PanicButton.Click += (_, _) => TogglePanicFromButton();
        ClearLogButton.Click += (s, e) =>
        {
            _chatFollowTail = true;
            _chatMessagesAddedWhileScrolledUp = 0;
            _chatViewModel.ClearChat();
            UpdateJumpToLatestChatUi();
        };

        Opened += (_, _) => ChatInputTextBox.Focus();

        Task.Run(async () => await _agentService.InitializeAgentsAsync(CancellationToken.None));
        _ = Task.Run(CheckForBackgroundAppUpdatesAsync);
    }

    private async Task CheckForBackgroundAppUpdatesAsync()
    {
        try
        {
            var settings = _settingsManager.Load(_apiKeyStore);
            if (!settings.EnableAutoUpdateChecks)
                return;

            var result = await _appUpdateService.CheckForUpdatesAsync(CancellationToken.None).ConfigureAwait(false);
            if (!result.IsUpdateAvailable)
                return;

            Dispatcher.UIThread.Post(() =>
                _chatViewModel.AddMessage(
                    MessageRole.System,
                    $"Update available ({result.LatestVersion}). Open Settings > Updates to install.",
                    "System"));
        }
        catch
        {
            // Best-effort background check.
        }
    }

    private async void OnReloadPluginsClick(object? sender, RoutedEventArgs e) => await ReloadPluginsAsync();
    private void OnSettingsClick(object? sender, RoutedEventArgs e) => OpenSettings();
    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private static void OnWorkspaceTreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem tvi || tvi.DataContext is not ArtifactViewModel vm)
            return;
        vm.IsExpanded = true;
        vm.RunPendingLoadIfAny();
    }

    private static void OnWorkspaceTreeItemCollapsed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem tvi || tvi.DataContext is not ArtifactViewModel vm)
            return;
        vm.IsExpanded = false;
    }

    private void Artifact_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ArtifactViewModel artifact && !artifact.IsPlaceholder)
        {
            artifact.OpenFileCommand.Execute(null);
        }
    }

    /// <summary>Called from the graph when an agent node is double-clicked.</summary>
    public void OpenAgentEditorFromGraph(string agentTitle)
    {
        var agentName = agentTitle;
        var existingTab = _viewModel.Tabs.FirstOrDefault(t =>
            string.Equals(t.Header, agentName, StringComparison.OrdinalIgnoreCase));
        if (existingTab != null)
        {
            _viewModel.SelectedTab = existingTab;
            return;
        }

        if (_agentService.Agents.ContainsKey(agentName))
            OpenAgentEditorTab(agentName);
    }

    private void FinalizeAndAddDuplicate(AgentDefinition draft, string sourceLabel)
    {
        var uniqueName = _agentService.MakeUniqueAgentName(draft.Name);
        var newDef = new AgentDefinition
        {
            Name = uniqueName,
            Role = draft.Role,
            Description = draft.Description,
            Instructions = draft.Instructions,
            NextAgentName = draft.NextAgentName,
            EnabledTools = draft.EnabledTools.ToList(),
            AccentColor = _agentService.PickNextAvailableAccentColor(),
            LlmProvider = draft.LlmProvider,
            ModelId = draft.ModelId
        };
        _agentService.AddAgent(newDef);
        OpenAgentEditorTab(uniqueName);
        Dispatcher.UIThread.Post(() => EnsureAgentVisibleOnGraph(uniqueName), DispatcherPriority.Loaded);
    }

    private static Point SnapGrid(Point p) =>
        new(Math.Round(p.X / GridSnap) * GridSnap, Math.Round(p.Y / GridSnap) * GridSnap);

    private static Point GridPlacementForIndex(int index)
    {
        var row = index / 3;
        var col = index % 3;
        var x = Math.Round((50.0 + col * 450) / GridSnap) * GridSnap;
        var y = Math.Round((50.0 + row * 180) / GridSnap) * GridSnap;
        return new Point(x, y);
    }

    /// <summary>True if axis-aligned node boxes are closer than <paramref name="gutter"/> edge-to-edge (forbidden).</summary>
    private static bool NodeBoxesCollideWithGutter(Point aTopLeft, Point bTopLeft, double gutter)
    {
        var ax2 = aTopLeft.X + AgentNodeWidth;
        var ay2 = aTopLeft.Y + AgentNodeHeight;
        var bx2 = bTopLeft.X + AgentNodeWidth;
        var by2 = bTopLeft.Y + AgentNodeHeight;

        var separatedX = ax2 + gutter <= bTopLeft.X || bx2 + gutter <= aTopLeft.X;
        var separatedY = ay2 + gutter <= bTopLeft.Y || by2 + gutter <= aTopLeft.Y;
        return !(separatedX || separatedY);
    }

    private bool AgentNodeOverlapsAny(Point candidateTopLeft, AgentNodeViewModel? moving)
    {
        foreach (var node in _viewModel.Nodes)
        {
            if (node is not AgentNodeViewModel other)
                continue;
            if (moving != null && ReferenceEquals(other, moving))
                continue;
            if (NodeBoxesCollideWithGutter(candidateTopLeft, other.Location, NodeLayoutGutter))
                return true;
        }

        return false;
    }

    /// <summary>Snapped position nearest to <paramref name="desiredSnapped"/> that does not collide with other nodes.</summary>
    private Point FindNearestNonOverlappingGridPosition(Point desiredSnapped, AgentNodeViewModel? moving)
    {
        if (!AgentNodeOverlapsAny(desiredSnapped, moving))
            return desiredSnapped;

        var gx = (int)Math.Round(desiredSnapped.X / GridSnap);
        var gy = (int)Math.Round(desiredSnapped.Y / GridSnap);

        const int maxRings = 600;
        for (var r = 1; r <= maxRings; r++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                for (var dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        continue;
                    var c = new Point((gx + dx) * GridSnap, (gy + dy) * GridSnap);
                    if (!AgentNodeOverlapsAny(c, moving))
                        return c;
                }
            }
        }

        return desiredSnapped;
    }

    /// <summary>Prefer persisted <see cref="IStateStore"/> layout, then optional viewport center, then grid slot; never overlaps existing nodes.</summary>
    private Point ResolveNodeLocation(string agentName, int fallbackGridIndex, Point? viewportCenter)
    {
        Point raw;
        if (_session.State.TryGetNodeLayout(agentName, out var persisted))
            raw = new Point(persisted.X, persisted.Y);
        else if (viewportCenter is { } c)
            raw = c;
        else
            raw = GridPlacementForIndex(fallbackGridIndex);

        var snapped = SnapGrid(raw);
        return FindNearestNonOverlappingGridPosition(snapped, moving: null);
    }

    private void WireNodeLayoutPersistence(AgentNodeViewModel node)
    {
        node.PropertyChanged += OnAgentNodeLayoutPropertyChanged;
    }

    private void OnAgentNodeLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AgentNodeViewModel.Location))
            return;
        if (sender is not AgentNodeViewModel n || string.IsNullOrEmpty(n.AgentKey))
            return;

        if (_applyingNodeLayoutCorrection)
        {
            var p = n.Location;
            _session.State.SetNodeLayout(n.AgentKey, new NodePosition(p.X, p.Y));
            return;
        }

        var snapped = SnapGrid(n.Location);
        var resolved = FindNearestNonOverlappingGridPosition(snapped, n);
        if (resolved != n.Location)
        {
            _applyingNodeLayoutCorrection = true;
            try
            {
                n.Location = resolved;
            }
            finally
            {
                _applyingNodeLayoutCorrection = false;
            }

            return;
        }

        _session.State.SetNodeLayout(n.AgentKey, new NodePosition(n.Location.X, n.Location.Y));
    }

    private static List<string> GetPropertyNamesFromSchema(string? schemaJson)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(schemaJson)) return names;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(schemaJson);
            if (doc.RootElement.TryGetProperty("properties", out var props) && props.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in props.EnumerateObject())
                {
                    names.Add(prop.Name);
                }
            }
        }
        catch { /* ignore invalid schema */ }
        return names;
    }

    private static void WireAgentConnectors(AgentNodeViewModel node, AgentDefinition def)
    {
        node.Input.Clear();
        node.Output.Clear();

        bool isOrchestrator = string.Equals(def.Name, AgentService.OrchestratorAgentName, StringComparison.OrdinalIgnoreCase);

        // 1. Standard In/Out
        node.Input.Add(new AgentConnectorViewModel
        {
            Host = node,
            Title = "In",
            PortToolTip = "Main task flow. Alt+click: disconnect."
        });

        if (!isOrchestrator)
        {
            node.Input.Add(new AgentConnectorViewModel
            {
                Host = node,
                Title = "Feedback",
                PortToolTip = "Logical source for rejections. Drag to a Return port. Alt+click: disconnect."
            });
        }

        node.Output.Add(new AgentConnectorViewModel
        {
            Host = node,
            Title = "Out",
            PortToolTip = "Main success flow. Alt+click: disconnect."
        });

        if (!isOrchestrator)
        {
            node.Output.Add(new AgentConnectorViewModel
            {
                Host = node,
                Title = "Return",
                PortToolTip = "Logical target for rejections. Receives critique from a Feedback port. Alt+click: disconnect."
            });
        }

        // 2. Dynamic Tool Ports (NOT rendered on node - managed in Agent Editor)
        // (Removing old tool-to-input-port logic)

        // 3. Dynamic Schema Ports (Outputs)
        var schemaProps = GetPropertyNamesFromSchema(def.JsonSchema);
        foreach (var prop in schemaProps)
        {
            node.Output.Add(new AgentConnectorViewModel
            {
                Host = node,
                Title = prop,
                PortToolTip = $"Property: '{prop}' from JSON Schema."
            });
        }
    }

    /// <summary>Second input: logical source for conditional send-back (Feedback in the UI).</summary>
    private static bool IsFeedbackOutputConnector(AgentConnectorViewModel src) =>
        src.Host != null
        && src.Host.Input.Count > 1
        && ReferenceEquals(src.Host.Input[1], src);

    /// <summary>Second output: logical target for conditional send-back (Return in the UI).</summary>
    private static bool IsReturnInputConnector(AgentConnectorViewModel tgt) =>
        tgt.Host != null
        && tgt.Host.Output.Count > 1
        && ReferenceEquals(tgt.Host.Output[1], tgt);
    private async Task<ToolApprovalUiResult> ShowWriteApprovalDialogAsync(string toolId, string details)
    {
        var detailsBlock = new TextBlock
        {
            Text = details,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480
        };
        var sessionCb = new CheckBox
        {
            Content = "Always allow for this session",
            Margin = new Thickness(0, 14, 0, 0)
        };
        var panel = new StackPanel { Children = { detailsBlock, sessionCb } };
        var dialog = new ContentDialog
        {
            Title = $"Approve {toolId}?",
            Content = panel,
            PrimaryButtonText = "Approve",
            CloseButtonText = "Deny",
            DefaultButton = ContentDialogButton.Close
        };
        var btn = await dialog.ShowAsync(this).ConfigureAwait(true);
        var approved = btn == ContentDialogResult.Primary;
        return new ToolApprovalUiResult(approved, approved && sessionCb.IsChecked == true);
    }

    /// <summary>Called from <see cref="Views.GraphTabView"/> when the Nodify editor loads.</summary>
    internal void AttachGraphEditor(NodifyEditor editor)
    {
        editor.ConnectionCompletedCommand = new RelayCommand<object?>(HandleGraphConnectionCompleted);
        editor.RemoveConnectionCommand = new RelayCommand<object?>(HandleGraphConnectionRemoved);
        editor.DisconnectConnectorCommand = new RelayCommand<object?>(HandleGraphConnectorDisconnect);
    }

    private void HandleGraphConnectionCompleted(object? parameter)
    {
        if (!TryResolveDirectedConnection(parameter, out var src, out var tgt))
            return;
        if (src.Host == null || tgt.Host == null || ReferenceEquals(src.Host, tgt.Host))
            return;

        var isFeedbackOut = IsFeedbackOutputConnector(src);
        var isReturnIn = IsReturnInputConnector(tgt);
        if (isFeedbackOut != isReturnIn)
            return;

        if (isFeedbackOut)
            _agentService.SetReturnHandoffTarget(src.Host.AgentKey, tgt.Host.AgentKey);
        else
            _agentService.SetHandoffTarget(src.Host.AgentKey, tgt.Host.AgentKey);
    }

    private void HandleGraphConnectionRemoved(object? parameter)
    {
        if (parameter is not HandoffConnectionViewModel hc)
            return;
        var from = hc.Source.Host?.AgentKey;
        if (string.IsNullOrEmpty(from))
            return;
        if (hc.IsConditionalReturn)
            _agentService.SetReturnHandoffTarget(from, null);
        else
            _agentService.SetHandoffTarget(from, AgentService.OrchestratorAgentName);
    }

    /// <summary>Nodify default: Alt+left-click on a connector. Clears outbound handoff from that port, or any inbound edge to an input port.</summary>
    private void HandleGraphConnectorDisconnect(object? parameter)
    {
        var vm = AsConnectorViewModel(parameter);
        if (vm?.Host == null || string.IsNullOrEmpty(vm.Host.AgentKey))
            return;

        var key = vm.Host.AgentKey;
        
        // 1. Check if it's a Source (Outbound)
        bool isDefaultOut = vm.Host.Output.IndexOf(vm) == 0;
        bool isFeedbackOut = IsFeedbackOutputConnector(vm);

        if (isDefaultOut)
        {
            _agentService.SetHandoffTarget(key, AgentService.OrchestratorAgentName);
            return;
        }
        if (isFeedbackOut)
        {
            _agentService.SetReturnHandoffTarget(key, null);
            return;
        }

        // 2. Check if it's a Target (Inbound)
        bool isDefaultIn = vm.Host.Input.IndexOf(vm) == 0;
        bool isReturnIn = IsReturnInputConnector(vm);

        if (isReturnIn)
            _agentService.RemoveInboundReturnEdgesTo(key);
        else if (isDefaultIn)
            _agentService.RemoveInboundDefaultEdgesTo(key);
    }

    /// <summary>
    /// Nodify may pass <see cref="ConnectionCompletedCommand"/> endpoints in either order; map to output→input so
    /// dragging Return→Feedback is accepted regardless of tuple ordering.
    /// </summary>
    private static bool TryResolveDirectedConnection(object? parameter, out AgentConnectorViewModel srcOut, out AgentConnectorViewModel tgtIn)
    {
        srcOut = null!;
        tgtIn = null!;
        if (!TryGetConnectionTupleItems(parameter, out var item1, out var item2))
            return false;
        var a = AsConnectorViewModel(item1);
        var b = AsConnectorViewModel(item2);
        if (a?.Host == null || b?.Host == null || ReferenceEquals(a.Host, b.Host))
            return false;

        // 1. Standard Forward Flow: Output[0] (Source) -> Input[0] (Target)
        if (a.Host.Output.IndexOf(a) == 0 && b.Host.Input.IndexOf(b) == 0)
        {
            srcOut = a;
            tgtIn = b;
            return true;
        }
        if (b.Host.Output.IndexOf(b) == 0 && a.Host.Input.IndexOf(a) == 0)
        {
            srcOut = b;
            tgtIn = a;
            return true;
        }

        // 2. Return/Feedback Flow: Input[1] (Feedback Source) -> Output[1] (Return Target)
        if (IsFeedbackOutputConnector(a) && IsReturnInputConnector(b))
        {
            srcOut = a;
            tgtIn = b;
            return true;
        }
        if (IsFeedbackOutputConnector(b) && IsReturnInputConnector(a))
        {
            srcOut = b;
            tgtIn = a;
            return true;
        }

        return false;
    }

    /// <summary>Nodify passes <c>(object, object?)</c> as a <see cref="ValueTuple{T1,T2}"/>, not <see cref="Tuple{T1,T2}"/>.</summary>
    private static bool TryGetConnectionTupleItems(object? parameter, out object? item1, out object? item2)
    {
        item1 = null;
        item2 = null;
        switch (parameter)
        {
            case Tuple<object, object> t:
                item1 = t.Item1;
                item2 = t.Item2;
                return true;
            case ValueTuple<object, object?> vt:
                item1 = vt.Item1;
                item2 = vt.Item2;
                return true;
            case ITuple { Length: 2 } it:
                item1 = it[0];
                item2 = it[1];
                return true;
            default:
                return false;
        }
    }

    private static AgentConnectorViewModel? AsConnectorViewModel(object? o)
    {
        if (o is AgentConnectorViewModel vm)
            return vm;
        if (o is Connector c)
            return c.DataContext as AgentConnectorViewModel;
        return null;
    }

    private void RebuildHandoffConnections()
    {
        foreach (var c in _viewModel.Connections.ToList())
        {
            c.PropertyChanged -= HandleEdgePropertyChanged;
            c.Detach();
            _viewModel.Connections.Remove(c);
        }

        var edges = _agentService.GetEffectiveWorkflowEdges();

        foreach (var node in _viewModel.Nodes.OfType<AgentNodeViewModel>())
        {
            if (string.IsNullOrEmpty(node.AgentKey) || node.Output.Count == 0 || node.Input.Count == 0)
                continue;

            var def = edges.FirstOrDefault(e =>
                string.Equals(e.From, node.AgentKey, StringComparison.OrdinalIgnoreCase)
                && e.Kind == WorkflowEdgeKind.Default);
            if (def != null
                && !string.IsNullOrWhiteSpace(def.To)
                && !def.To.Equals(AgentService.OrchestratorAgentName, StringComparison.OrdinalIgnoreCase))
            {
                var toNode = _viewModel.Nodes.OfType<AgentNodeViewModel>()
                    .FirstOrDefault(n => string.Equals(n.AgentKey, def.To, StringComparison.OrdinalIgnoreCase));
                if (toNode != null && toNode.Input.Count > 0)
                {
                    var c = new HandoffConnectionViewModel(
                        node.Output[0],
                        toNode.Input[0],
                        isConditionalReturn: false,
                        edgeLabel: def.Label,
                        conditionRef: def.ConditionRef);
                    c.PropertyChanged += HandleEdgePropertyChanged;
                    _viewModel.Connections.Add(c);
                }
            }

            if (node.Input.Count > 1)
            {
                var ret = edges.FirstOrDefault(e =>
                    string.Equals(e.From, node.AgentKey, StringComparison.OrdinalIgnoreCase)
                    && e.Kind == WorkflowEdgeKind.Return);
                if (ret != null
                    && !string.IsNullOrWhiteSpace(ret.To)
                    && !ret.To.Equals(AgentService.OrchestratorAgentName, StringComparison.OrdinalIgnoreCase))
                {
                    var toNode = _viewModel.Nodes.OfType<AgentNodeViewModel>()
                        .FirstOrDefault(n => string.Equals(n.AgentKey, ret.To, StringComparison.OrdinalIgnoreCase));
                    if (toNode != null && toNode.Output.Count > 1)
                    {
                        var c = new HandoffConnectionViewModel(
                            node.Input[1],
                            toNode.Output[1],
                            isConditionalReturn: true,
                            edgeLabel: ret.Label,
                            conditionRef: ret.ConditionRef);
                        c.PropertyChanged += HandleEdgePropertyChanged;
                        _viewModel.Connections.Add(c);
                    }
                }
            }
        }
    }

    private void HandleEdgePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is HandoffConnectionViewModel hc)
        {
            if (e.PropertyName == nameof(HandoffConnectionViewModel.EdgeLabel) ||
                e.PropertyName == nameof(HandoffConnectionViewModel.ConditionRef))
            {
                var from = hc.Source.Host?.AgentKey;
                var to = hc.Target.Host?.AgentKey;
                if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                {
                    _agentService.UpdateWorkflowEdgeProperties(from, to, hc.IsConditionalReturn, hc.EdgeLabel, hc.ConditionRef);
                }
            }
        }
    }

    private void PulseHandoffConnection(string fromKey, string toKey, bool isReturn)
    {
        foreach (var c in _viewModel.Connections)
        {
            if (string.Equals(c.Source.Host?.AgentKey, fromKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Target.Host?.AgentKey, toKey, StringComparison.OrdinalIgnoreCase)
                && c.IsConditionalReturn == isReturn)
            {
                c.PulseFlow();
                return;
            }
        }
    }

    private NodifyEditor? TryFindGraphNodifyEditor() =>
        this.GetVisualDescendants().OfType<NodifyEditor>().FirstOrDefault();

    private AgentNodeViewModel? TryGetAgentNode(string name) =>
        _viewModel.Nodes.OfType<AgentNodeViewModel>()
            .FirstOrDefault(n => string.Equals(n.Title, name, StringComparison.OrdinalIgnoreCase));

    private static Point? TryGetViewportCenterPlacement(NodifyEditor editor)
    {
        var vp = editor.ViewportLocation;
        var vs = editor.ViewportSize;
        if (vs.Width <= 0 || vs.Height <= 0)
            return null;
        var x = vp.X + vs.Width / 2 - AgentNodeWidth / 2;
        var y = vp.Y + vs.Height / 2 - AgentNodeHeight / 2;
        return SnapGrid(new Point(x, y));
    }

    private void TryBringAgentNodeIntoViewCenter(NodifyEditor editor, string agentTitle)
    {
        var node = _viewModel.Nodes.OfType<AgentNodeViewModel>()
            .FirstOrDefault(n => string.Equals(n.Title, agentTitle, StringComparison.OrdinalIgnoreCase));
        if (node == null)
            return;
        var center = new Point(node.Location.X + AgentNodeWidth / 2, node.Location.Y + AgentNodeHeight / 2);
        editor.BringIntoView(center, animated: true);
    }

    /// <summary>
    /// Pans the graph so the agent node is in view. Temporarily selects THE GRID tab if the editor is not realized.
    /// </summary>
    private void EnsureAgentVisibleOnGraph(string agentCanonicalName)
    {
        var titleUpper = agentCanonicalName.ToUpperInvariant();

        var ed = TryFindGraphNodifyEditor();
        if (ed != null)
        {
            TryBringAgentNodeIntoViewCenter(ed, titleUpper);
            return;
        }

        if (_viewModel.Tabs.Count == 0)
            return;

        var graphTab = _viewModel.Tabs[0];
        var saved = _viewModel.SelectedTab;
        _viewModel.SelectedTab = graphTab;
        Dispatcher.UIThread.Post(() =>
        {
            ed = TryFindGraphNodifyEditor();
            if (ed != null)
                TryBringAgentNodeIntoViewCenter(ed, titleUpper);
            if (saved != null)
                _viewModel.SelectedTab = saved;
        }, DispatcherPriority.Loaded);
    }

    private void AgentService_OnOrchestratorPromptUpdated(string fullPrompt)
    {
        Dispatcher.UIThread.Post(() => _orchestratorEditorVm?.ApplyRuntimeSystemPrompt(fullPrompt));
    }

    private void AgentService_OnAgentRenamed(string oldName, string newName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var ho = oldName.ToUpperInvariant();
            var hn = newName.ToUpperInvariant();
            var tab = _viewModel.Tabs.FirstOrDefault(t => string.Equals(t.Header, ho, StringComparison.OrdinalIgnoreCase));
            if (tab != null)
                tab.Header = hn;
            var node = _viewModel.Nodes.OfType<AgentNodeViewModel>()
                .FirstOrDefault(n => string.Equals(n.Title, ho, StringComparison.OrdinalIgnoreCase));
            if (node != null)
            {
                node.Title = hn;
                node.AgentKey = newName;
            }

            _session.State.RenameNodeLayout(oldName, newName);

            foreach (var t in _viewModel.Tabs)
            {
                if (t.Content is AgentEditorViewModel vm
                    && string.Equals(vm.PersistenceKey, oldName, StringComparison.OrdinalIgnoreCase))
                    vm.SetPersistenceKey(newName);
            }

            _agentService.RenameModelStreamMonitorKey(oldName, newName);
        });
    }

    private void RemoveAgentFromUi(string canonicalName)
    {
        var header = canonicalName.ToUpperInvariant();
        var tab = _viewModel.Tabs.FirstOrDefault(t => t.Header == header);
        if (tab != null)
            _viewModel.Tabs.Remove(tab);

        var node = _viewModel.Nodes.OfType<AgentNodeViewModel>()
            .FirstOrDefault(n => string.Equals(n.Title, header, StringComparison.OrdinalIgnoreCase));
        if (node != null)
            _viewModel.Nodes.Remove(node);

        if (_viewModel.SelectedTab != null && !_viewModel.Tabs.Contains(_viewModel.SelectedTab))
            _viewModel.SelectedTab = _viewModel.Tabs.Count > 0 ? _viewModel.Tabs[0] : null;
    }

    private void SyncAgentNodeHeader(string agentName)
    {
        var node = _viewModel.Nodes.OfType<AgentNodeViewModel>()
            .FirstOrDefault(n => string.Equals(n.AgentKey, agentName, StringComparison.OrdinalIgnoreCase));
        if (node == null || !_agentService.Agents.TryGetValue(agentName, out var ag))
            return;
        node.HeaderBrush = AgentAccentOptions.ParseHeaderBrush(ag.Definition.AccentColor);
        node.Title = ag.Definition.Name.ToUpper();
        
        // Refresh dynamic ports based on updated tools/schema
        WireAgentConnectors(node, ag.Definition);
        RebuildHandoffConnections();
    }

    private async Task<bool> ConfirmDuplicateAgentAsync(string sourceAgentName)
    {
        var dialog = new ContentDialog
        {
            Title = "Duplicate agent",
            Content = $"Create a copy of '{sourceAgentName}'? It will get a unique name and open in a new tab.",
            PrimaryButtonText = "Duplicate",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmPurgeArtifactAsync(ArtifactViewModel artifact)
    {
        var itemLabel = artifact.IsDirectory ? "folder" : "file";
        var headline = new TextBlock
        {
            Text = $"Permanently purge this {itemLabel}?",
            Foreground = Brushes.IndianRed,
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460
        };
        var body = new TextBlock
        {
            Text = $"This will permanently delete '{artifact.FileName}' from disk.\n\nPath:\n{artifact.FullPath}\n\nThis action cannot be undone.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var dialog = new ContentDialog
        {
            Title = "Are you sure?",
            Content = new StackPanel { Children = { headline, body } },
            PrimaryButtonText = "Purge permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary;
    }

    private void OpenAgentSessionLogTab(string agentKey)
    {
        var existing = _viewModel.Tabs.FirstOrDefault(t =>
            t.Content is AgentLogViewModel log
            && string.Equals(log.AgentKey, agentKey, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _viewModel.SelectedTab = existing;
            return;
        }

        var monitor = _agentService.GetOrCreateModelStreamMonitor(agentKey);
        var logVm = new AgentLogViewModel(agentKey, monitor);
        var tab = new TabItemViewModel
        {
            Header = $"{agentKey.ToUpperInvariant()} · LOG",
            Content = logVm,
            IsClosable = true
        };
        _viewModel.Tabs.Add(tab);
        _viewModel.SelectedTab = tab;
    }

    private void OpenAgentEditorTab(string agentLookupKey)
    {
        if (!_agentService.Agents.TryGetValue(agentLookupKey, out var agent))
            return;

        var canonicalName = agent.Definition.Name;
        var header = canonicalName.ToUpperInvariant();
        var existingTab = _viewModel.Tabs.FirstOrDefault(t => t.Header == header);
        if (existingTab != null)
        {
            _viewModel.SelectedTab = existingTab;
            return;
        }

        var allAgentNames = _agentService.Agents.Keys.ToList();
        var allToolIds = _agentService.AvailableTools.Keys.ToList();
        var editorVM = new AgentEditorViewModel(_agentService, agent.Definition, allAgentNames, allToolIds);
        editorVM.OnChanged += newDef =>
        {
            _agentService.UpdateAgent(editorVM.PersistenceKey, newDef);
            Dispatcher.UIThread.Post(RebuildHandoffConnections);
        };
        editorVM.OnOpenSessionLogRequested += OpenAgentSessionLogTab;
        editorVM.OnAccentPersistRequested += (n, h) => _agentService.SetAgentAccent(n, h);
        editorVM.OnDuplicateRequested += (draft) => FinalizeAndAddDuplicate(draft, editorVM.PersistenceKey);
        editorVM.ConfirmDuplicateAsync = () => ConfirmDuplicateAgentAsync(editorVM.Name);
        editorVM.ConfirmDeleteAsync = async () =>
        {
            var key = editorVM.PersistenceKey;
            var headline = new TextBlock
            {
                Text = "This cannot be undone.",
                Foreground = Brushes.IndianRed,
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            };
            var body = new TextBlock
            {
                Text = $"The agent '{key}' will be removed from the graph and deleted from agents.json. " +
                       "Any other agent that hands off to it will be switched to Orchestrator instead.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var dialog = new ContentDialog
            {
                Title = "Delete this agent?",
                Content = new StackPanel { Children = { headline, body } },
                PrimaryButtonText = "Delete permanently",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary;
        };
        editorVM.OnDeleteConfirmed += () =>
        {
            var key = editorVM.PersistenceKey;
            if (!_agentService.RemoveAgent(key))
                return;
            RemoveAgentFromUi(key);
        };
        editorVM.OnCloseRequested += () => CloseTabForContent(editorVM);

        var newTab = new TabItemViewModel { Header = header, Content = editorVM, IsClosable = true };
        _viewModel.Tabs.Add(newTab);
        _viewModel.SelectedTab = newTab;
    }

    private void MainTabCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: TabItemViewModel tab })
            return;
        if (!tab.IsClosable)
            return;
        CloseTabItem(tab);
    }

    private void CloseTabItem(TabItemViewModel tab)
    {
        if (!_viewModel.Tabs.Contains(tab))
            return;
        if (tab.Content is PluginSettingsViewModel pluginSettingsVm)
            pluginSettingsVm.FlushSave();
        var wasSelected = ReferenceEquals(_viewModel.SelectedTab, tab);
        _viewModel.Tabs.Remove(tab);
        if (wasSelected)
            _viewModel.SelectedTab = _viewModel.Tabs.Count > 0 ? _viewModel.Tabs[0] : null;
    }

    private void CloseTabForContent(object content)
    {
        var tab = _viewModel.Tabs.FirstOrDefault(t => ReferenceEquals(t.Content, content));
        if (tab != null)
            CloseTabItem(tab);
    }

    private void ChatMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && !_chatFollowTail)
            _chatMessagesAddedWhileScrolledUp += e.NewItems?.Count ?? 1;

        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove
            or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Reset)
            ScrollChatToEndIfFollowing();

        UpdateJumpToLatestChatUi();
    }

    private void ChatViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsThinking))
            ScrollChatToEndIfFollowing();
    }

    private void ChatScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (IsChatScrolledToBottom())
        {
            _chatFollowTail = true;
            _chatMessagesAddedWhileScrolledUp = 0;
            UpdateJumpToLatestChatUi();
            return;
        }

        // Taller content with the same offset: still following until we scroll on next layout tick.
        if (_chatFollowTail && e.ExtentDelta.Y > 0 && e.OffsetDelta.Y == 0)
            return;

        _chatFollowTail = false;
        UpdateJumpToLatestChatUi();
    }

    private bool IsChatScrolledToBottom()
    {
        var sv = ChatScrollViewer;
        var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        return maxY - sv.Offset.Y <= ChatScrollBottomThreshold;
    }

    private void ScrollChatToEndIfFollowing()
    {
        if (!_chatFollowTail)
            return;
        Dispatcher.UIThread.Post(() => ChatScrollViewer.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void UpdateJumpToLatestChatUi()
    {
        var show = !_chatFollowTail && _chatViewModel.Messages.Count > 0;
        JumpToLatestChatButton.IsVisible = show;
        if (!show)
            return;
        JumpToLatestChatButton.Content = _chatMessagesAddedWhileScrolledUp > 0
            ? $"Jump to latest ({_chatMessagesAddedWhileScrolledUp})"
            : "Jump to latest";
    }

    private void JumpToLatestChat_Click(object? sender, RoutedEventArgs e)
    {
        _chatFollowTail = true;
        _chatMessagesAddedWhileScrolledUp = 0;
        UpdateJumpToLatestChatUi();
        Dispatcher.UIThread.Post(() => ChatScrollViewer.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void ChatInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;
            if (_chatViewModel.SendMessageCommand.CanExecute(null))
            {
                _chatViewModel.SendMessageCommand.Execute(null);
            }
        }
    }

    private void ApplyThinkingAgentDisplay(string agentName)
    {
        _chatViewModel.ThinkingAgentName = agentName;
        _chatViewModel.ThinkingAgentAccentHex = _agentService.Agents.TryGetValue(agentName, out var ag)
            ? ag.Definition.AccentColor
            : null;
    }

    private void SyncThinkingIndicator()
    {
        if (_busyAgents.Count == 0)
        {
            _chatViewModel.IsThinking = false;
            return;
        }

        var activeName = _busyAgents.Contains(AgentService.OrchestratorAgentName)
            ? AgentService.OrchestratorAgentName
            : _busyAgents.First();

        ApplyThinkingAgentDisplay(activeName);
        _chatViewModel.IsThinking = true;
    }

    private async Task RunTask(string taskText)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StatusLabel.Text = "STATUS: AGENTS RUNNING";
            StatusLabel.Foreground = Brushes.Cyan;
            _busyAgents.Clear();
            _busyAgents.Add(AgentService.OrchestratorAgentName);
            ApplyThinkingAgentDisplay(AgentService.OrchestratorAgentName);
            _chatViewModel.IsThinking = true;
        });

        try
        {
            await Task.Run(async () =>
                    await _agentService.ProcessChatInput(taskText, _cts.Token).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _busyAgents.Clear();
                _chatViewModel.IsThinking = false;
                if (!_panicActive)
                {
                    StatusLabel.Text = "STATUS: STANDBY";
                    StatusLabel.Foreground = Brushes.SpringGreen;
                }
            });
        }
    }

    private void OpenSettings()
    {
        var existing = _viewModel.Tabs.FirstOrDefault(t => t.Content is SettingsViewModel);
        if (existing != null)
        {
            _viewModel.SelectedTab = existing;
            return;
        }

        var settingsVM = new SettingsViewModel(_settingsManager, _apiKeyStore, _agentService, _appUpdateService, OpenPluginSettingsTab);
        var newTab = new TabItemViewModel 
        { 
            Header = "SETTINGS", 
            Content = settingsVM, 
            IsClosable = true
        };
        _viewModel.Tabs.Add(newTab);
        _viewModel.SelectedTab = newTab;
    }

    private void OpenPluginSettingsTab(Sharpwire.Core.MetaToolbox.PluginSettingInfo info)
    {
        var existing = _viewModel.Tabs.FirstOrDefault(t => t.Content is PluginSettingsViewModel p && string.Equals(p.PluginName, info.PluginName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _viewModel.SelectedTab = existing;
            return;
        }

        var appSettings = _settingsManager.Load(_apiKeyStore);
        appSettings.PluginSettings.TryGetValue(info.PluginName, out var stored);
        var current = stored != null
            ? new Dictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var vm = new PluginSettingsViewModel(info, _settingsManager, current, _agentService);
        var tab = new TabItemViewModel
        {
            Header = $"{info.PluginName} (plugin)",
            Content = vm,
            IsClosable = true
        };
        _viewModel.Tabs.Add(tab);
        _viewModel.SelectedTab = tab;
    }

    private async Task ReloadPluginsAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        try
        {
            await _agentService.InitializeAgentsAsync(_cts.Token);
            _chatViewModel.AddMessage(MessageRole.System, "New plugins compiled and agents reinitialized.", "System");
        }
        catch (Exception ex)
        {
            _chatViewModel.AddMessage(MessageRole.System, $"Error: {ex.Message}", "System");
        }
    }

    private void TogglePanicFromButton()
    {
        _panicActive = !_panicActive;
        if (_panicActive)
        {
            _cts?.Cancel();
            _cts = null;
            StatusLabel.Text = "STATUS: INTERRUPTED";
            StatusLabel.Foreground = Brushes.Red;
            PanicButton.Content = "PANIC · ON";
            PanicButton.Classes.Add("panic-active");
            _chatViewModel.AddMessage(MessageRole.System, "PANIC is on: the current agent run was canceled. Click PANIC again to turn it off.", "System");
        }
        else
        {
            StatusLabel.Text = "STATUS: STANDBY";
            StatusLabel.Foreground = Brushes.SpringGreen;
            PanicButton.Content = "PANIC";
            PanicButton.Classes.Remove("panic-active");
            _chatViewModel.AddMessage(MessageRole.System, "PANIC is off. You can run agents again.", "System");
        }
    }

    private async void OnSaveSceneClick(object? sender, RoutedEventArgs e)
    {
        var input = new TextBox { Watermark = "Scene Name (e.g. CodeProject_A)", MaxLength = 50 };
        var dialog = new ContentDialog
        {
            Title = "Save current scene",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            _agentService.SaveScene(input.Text.Trim());
        }
    }

    private async void OnLoadSceneClick(object? sender, RoutedEventArgs e)
    {
        var scenes = _agentService.GetSceneNames();
        if (scenes.Count == 0)
        {
            var msg = new ContentDialog
            {
                Title = "No scenes found",
                Content = "You haven't saved any scenes in this workspace yet.",
                CloseButtonText = "Close"
            };
            await msg.ShowAsync();
            return;
        }

        var listBox = new ListBox { ItemsSource = scenes, SelectedIndex = 0 };
        var dialog = new ContentDialog
        {
            Title = "Load scene",
            Content = listBox,
            PrimaryButtonText = "Load",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && listBox.SelectedItem is string name)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            await _agentService.LoadSceneAsync(name, _cts.Token);
            _chatViewModel.AddMessage(MessageRole.System, $"Scene '{name}' loaded.", "System");
        }
    }

    private async void OnClearSceneClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Clear current scene?",
            Content = "This will remove all custom agents, connections, and layouts. Built-in agents (Orchestrator, Coder, Reviewer) will be reset to defaults.",
            PrimaryButtonText = "Clear all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            await _agentService.ClearSceneAsync(_cts.Token);
            _chatViewModel.AddMessage(MessageRole.System, "Scene cleared.", "System");
        }
    }

    private async void OnAddAgentClick(object? sender, RoutedEventArgs e)
    {
        var roleInput = new TextBox
        {
            Watermark = "Role (e.g. Security Auditor, Release Engineer)",
            MaxLength = 80
        };
        var useLlmCheck = new CheckBox
        {
            Content = "Use LLM to suggest name, description, instructions, and tools",
            Margin = new Thickness(0, 12, 0, 0)
        };
        var hint = new TextBlock
        {
            Text = "Role is always saved. If LLM is off, other fields start empty (except a default graph name you can rename). If LLM is on, the configured model fills those fields—you can edit them in the editor next.",
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 420,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var panel = new StackPanel { Children = { roleInput, useLlmCheck, hint } };

        var dialog = new ContentDialog
        {
            Title = "Add New Agent",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(roleInput.Text))
            return;

        var role = roleInput.Text.Trim();
        var useLlm = useLlmCheck.IsChecked == true;
        var ct = _cts?.Token ?? CancellationToken.None;

        AgentDefinition? def;
        if (useLlm)
        {
            ApplyThinkingAgentDisplay(AgentService.OrchestratorAgentName);
            _chatViewModel.IsThinking = true;
            try
            {
                def = await _agentService.SuggestAgentDefinitionFromRoleAsync(role, ct).ConfigureAwait(true);
            }
            finally
            {
                _chatViewModel.IsThinking = false;
            }

            if (def == null)
            {
                var err = new ContentDialog
                {
                    Title = "Could not generate agent",
                    Content = "The model did not return usable JSON, or the LLM is not configured. Check Settings (API keys / provider), turn off LLM assist to create a blank agent, then try again.",
                    CloseButtonText = "OK"
                };
                await err.ShowAsync();
                return;
            }
        }
        else
        {
            def = new AgentDefinition
            {
                Name = _agentService.MakeUniqueAgentName("New agent"),
                Role = role,
                Description = string.Empty,
                Instructions = string.Empty,
                EnabledTools = new List<string>(),
                AccentColor = _agentService.PickNextAvailableAccentColor(),
                NextAgentName = AgentService.OrchestratorAgentName,
                IsChainEntry = true
            };
        }

        _agentService.AddAgent(def);
        OpenAgentEditorTab(def.Name);
        Dispatcher.UIThread.Post(() => EnsureAgentVisibleOnGraph(def.Name), DispatcherPriority.Loaded);
    }
}
