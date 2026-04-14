using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sharpwire.Core.Agents;

namespace Sharpwire.ViewModels;

public partial class OrchestratorEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "Orchestrator";

    [ObservableProperty]
    private string _role = "System Orchestrator";

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _instructions = string.Empty;

    [ObservableProperty]
    private string _accentColor = "#3D6B9A";

    [ObservableProperty]
    private AccentPreset? _selectedAccentPreset;

    public ObservableCollection<ToolOptionViewModel> Tools { get; } = new();

    public IReadOnlyList<AccentPreset> AccentPresets => AgentAccentOptions.Presets;

    public event Action<AgentDefinition>? OnChanged;
    public event Action<AgentDefinition>? OnDuplicateRequested;
    public event Action<string, string>? OnAccentPersistRequested;
    public event Action<string>? OnOpenSessionLogRequested;

    public Func<Task<bool>>? ConfirmDuplicateAsync { get; set; }

    private readonly string _defaultInstructions;

    public bool IsEditable => false; // Orchestrator is ALWAYS built-in/immutable

    /// <summary>Updates the editor text to match <see cref="Agent.DynamicInstructions"/> (agent list, handoffs, etc.).</summary>
    public void ApplyRuntimeSystemPrompt(string fullPrompt) => Instructions = fullPrompt;

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

    public OrchestratorEditorViewModel(AgentDefinition definition, IEnumerable<string> allToolIds)
    {
        _name = definition.Name;
        _role = definition.Role;
        _description = definition.Description;
        _instructions = definition.Instructions;
        _defaultInstructions = "You are the System Orchestrator. Your job is to listen to the user's request, and delegate tasks to the specialized agents available to you.";
        _accentColor = string.IsNullOrWhiteSpace(definition.AccentColor)
            ? "#3D6B9A"
            : definition.AccentColor.Trim();
        _selectedAccentPreset = AgentAccentOptions.Presets.FirstOrDefault(p =>
            p.Hex.Equals(_accentColor, StringComparison.OrdinalIgnoreCase));

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

    [RelayCommand]
    private void RevertToDefault()
    {
        Instructions = _defaultInstructions;
    }

    [RelayCommand]
    private void OpenSessionLog() => OnOpenSessionLogRequested?.Invoke("Orchestrator");

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
            NextAgentName = "Orchestrator",
            EnabledTools = Tools.Where(t => t.IsEnabled).Select(t => t.Id).ToList(),
            AccentColor = AccentColor,
            LlmProvider = string.Empty,
            ModelId = string.Empty
        };
        OnDuplicateRequested?.Invoke(copy);
    }

    partial void OnRoleChanged(string value) => TriggerChange();
    partial void OnDescriptionChanged(string value) => TriggerChange();
    partial void OnInstructionsChanged(string value) => TriggerChange();

    partial void OnSelectedAccentPresetChanged(AccentPreset? value)
    {
        if (value == null || AccentColor.Equals(value.Hex, StringComparison.OrdinalIgnoreCase))
            return;
        AccentColor = value.Hex;
    }

    partial void OnAccentColorChanged(string value) =>
        OnAccentPersistRequested?.Invoke(Name, AccentColor);

    private void TriggerChange()
    {
        if (!IsEditable) return;

        var newDef = new AgentDefinition
        {
            Name = Name,
            Role = Role,
            Description = Description,
            Instructions = Instructions,
            NextAgentName = "Orchestrator",
            EnabledTools = Tools.Where(t => t.IsEnabled).Select(t => t.Id).ToList(),
            AccentColor = AccentColor,
            LlmProvider = string.Empty,
            ModelId = string.Empty
        };
        OnChanged?.Invoke(newDef);
    }
}
