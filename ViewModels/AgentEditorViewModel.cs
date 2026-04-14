using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sharpwire.Core.Agents;

namespace Sharpwire.ViewModels;

public partial class ToolOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    public event Action? OnChanged;

    partial void OnIsEnabledChanged(bool value) => OnChanged?.Invoke();
}

public partial class AgentEditorViewModel : ObservableObject
{
    private readonly AgentService _agentService;

    /// <summary>Agent key when this editor was opened; used for built-in vs custom (rename does not change this).</summary>
    private readonly string _openedAsIdentity;

    /// <summary>Current key in <see cref="AgentService.Agents"/>; updated after a successful rename.</summary>
    private string _persistenceKey;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _role = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _instructions = string.Empty;

    [ObservableProperty]
    private string _nextAgentName = "Orchestrator";

    [ObservableProperty]
    private string _accentColor = "#0D7377";

    [ObservableProperty]
    private string? _jsonSchema;

    [ObservableProperty]
    private bool _isChainEntry;

    [ObservableProperty]
    private string? _llmProvider;

    [ObservableProperty]
    private string? _modelId;

    public ObservableCollection<string> GeminiModels { get; } = new();
    public ObservableCollection<string> OpenAiModels { get; } = new();
    public ObservableCollection<string> ClaudeModels { get; } = new();
    public string[] LlmProviders { get; } = { "", "Gemini", "OpenAI", "Anthropic", "Custom" };

    [ObservableProperty]
    private AccentPreset? _selectedAccentPreset;

    public ObservableCollection<string> AvailableAgents { get; } = new();
    public ObservableCollection<ToolOptionViewModel> Tools { get; } = new();

    public IReadOnlyList<AccentPreset> AccentPresets => AgentAccentOptions.Presets;

    public event Action<AgentDefinition>? OnChanged;
    public event Action<string, string>? OnAccentPersistRequested;
    public event Action<AgentDefinition>? OnDuplicateRequested;
    public event Action? OnDeleteConfirmed;
    public event Action? OnCloseRequested;
    public event Action<string>? OnOpenSessionLogRequested;

    /// <summary>Set by the host window to show a confirmation dialog before <see cref="OnDeleteConfirmed"/> runs.</summary>
    public Func<Task<bool>>? ConfirmDeleteAsync { get; set; }

    /// <summary>Set by the host window before duplicating (built-in agents only).</summary>
    public Func<Task<bool>>? ConfirmDuplicateAsync { get; set; }

    public bool IsBuiltIn => AgentService.IsBuiltInAgent(_openedAsIdentity);
    public bool IsEditable => !IsBuiltIn;
    public bool IsMetadataEditable => IsEditable;
    public bool CanDeleteAgent => IsEditable;

    public string PersistenceKey => _persistenceKey;

    public void SetPersistenceKey(string key) => _persistenceKey = key;

    public void RefreshTools(IEnumerable<string> allToolIds, List<string> enabledTools)
    {
        Tools.Clear();
        foreach (var toolId in allToolIds)
        {
            var toolOpt = new ToolOptionViewModel 
            { 
                Id = toolId, 
                IsEnabled = enabledTools.Contains(toolId) 
            };
            toolOpt.OnChanged += TriggerChange;
            Tools.Add(toolOpt);
        }
    }

    public AgentEditorViewModel(
        AgentService agentService,
        AgentDefinition definition,
        IEnumerable<string> allAgentNames,
        IEnumerable<string> allToolIds)
    {
        _agentService = agentService;
        _openedAsIdentity = definition.Name;
        _persistenceKey = definition.Name;
        _name = definition.Name;
        _role = definition.Role;
        _description = definition.Description;
        _instructions = definition.Instructions;
        _nextAgentName = definition.NextAgentName;
        _isChainEntry = definition.IsChainEntry;
        _jsonSchema = definition.JsonSchema;
        _llmProvider = definition.LlmProvider;
        _modelId = definition.ModelId;
        _accentColor = string.IsNullOrWhiteSpace(definition.AccentColor)
            ? "#0D7377"
            : definition.AccentColor.Trim();
        _selectedAccentPreset = AgentAccentOptions.Presets.FirstOrDefault(p =>
            p.Hex.Equals(_accentColor, StringComparison.OrdinalIgnoreCase));

        _agentService.OnModelsUpdated += HandleModelsUpdated;
        RefreshModelLists();

        foreach (var agent in allAgentNames)
        {
            if (agent != Name) AvailableAgents.Add(agent);
        }
        AvailableAgents.Insert(0, "Orchestrator");

        foreach (var toolId in allToolIds)
        {
            var toolOpt = new ToolOptionViewModel 
            { 
                Id = toolId, 
                IsEnabled = definition.EnabledTools.Contains(toolId) 
            };
            toolOpt.OnChanged += TriggerChange;
            Tools.Add(toolOpt);
        }
    }

    private void RefreshModelLists()
    {
        UpdateCollection(GeminiModels, _agentService.GeminiModels);
        UpdateCollection(OpenAiModels, _agentService.OpenAiModels);
        UpdateCollection(ClaudeModels, _agentService.ClaudeModels);
    }

    private void HandleModelsUpdated(string provider, List<string> models)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var currentModel = ModelId;
            if (provider == "Gemini") UpdateCollection(GeminiModels, models);
            else if (provider == "OpenAI") UpdateCollection(OpenAiModels, models);
            else if (provider == "Anthropic") UpdateCollection(ClaudeModels, models);
            ModelId = currentModel;
        });
    }

    private void UpdateCollection(ObservableCollection<string> collection, List<string> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    [RelayCommand]
    private void CloseAgentTab() => OnCloseRequested?.Invoke();

    [RelayCommand]
    private void OpenSessionLog() => OnOpenSessionLogRequested?.Invoke(PersistenceKey);

    [RelayCommand]
    private async Task DuplicateAgentAsync()
    {
        if (ConfirmDuplicateAsync is { } confirm && !await confirm().ConfigureAwait(true))
            return;
        var copy = new AgentDefinition
        {
            Name = $"{Name}_Copy",
            Role = $"{Role} (Copy)",
            Description = Description,
            Instructions = Instructions,
            NextAgentName = NextAgentName,
            IsChainEntry = IsChainEntry,
            JsonSchema = JsonSchema,
            EnabledTools = Tools.Where(t => t.IsEnabled).Select(t => t.Id).ToList(),
            AccentColor = AccentColor,
            LlmProvider = LlmProvider,
            ModelId = ModelId
        };
        OnDuplicateRequested?.Invoke(copy);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteAgent))]
    private async Task DeleteAgentAsync()
    {
        if (ConfirmDeleteAsync is not { } confirm || !await confirm().ConfigureAwait(true))
            return;
        OnDeleteConfirmed?.Invoke();
    }

    partial void OnNameChanged(string value) => TriggerChange();
    partial void OnRoleChanged(string value) => TriggerChange();
    partial void OnDescriptionChanged(string value) => TriggerChange();
    partial void OnInstructionsChanged(string value) => TriggerChange();
    partial void OnNextAgentNameChanged(string value) => TriggerChange();
    partial void OnIsChainEntryChanged(bool value) => TriggerChange();
    partial void OnJsonSchemaChanged(string? value) => TriggerChange();
    partial void OnLlmProviderChanged(string? value) => TriggerChange();
    partial void OnModelIdChanged(string? value) => TriggerChange();

    partial void OnSelectedAccentPresetChanged(AccentPreset? value)
    {
        if (value == null || AccentColor.Equals(value.Hex, StringComparison.OrdinalIgnoreCase))
            return;
        AccentColor = value.Hex;
    }

    partial void OnAccentColorChanged(string value)
    {
        if (IsBuiltIn)
            OnAccentPersistRequested?.Invoke(_persistenceKey, AccentColor);
        else
            TriggerChange();
    }

    private void TriggerChange()
    {
        if (!IsEditable) return;

        var newDef = new AgentDefinition
        {
            Name = Name,
            Role = Role,
            Description = Description,
            Instructions = Instructions,
            NextAgentName = NextAgentName,
            IsChainEntry = IsChainEntry,
            JsonSchema = JsonSchema,
            EnabledTools = Tools.Where(t => t.IsEnabled).Select(t => t.Id).ToList(),
            AccentColor = AccentColor,
            LlmProvider = LlmProvider,
            ModelId = ModelId
        };
        OnChanged?.Invoke(newDef);
    }
}
