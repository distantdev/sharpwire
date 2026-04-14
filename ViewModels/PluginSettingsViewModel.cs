using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sharpwire.Core;
using Sharpwire.Core.Agents;
using Sharpwire.Core.MetaToolbox;

namespace Sharpwire.ViewModels;

public partial class PluginPropertyViewModel : ObservableObject
{
    public string PropertyName { get; }
    public string Label { get; }
    public string Description { get; }
    public bool IsSecret { get; }
    public Type PropertyType { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool IsBoolEditorVisible => PropertyType == typeof(bool);

    public bool IsSecretPlainTextEditorVisible => !IsBoolEditorVisible && IsSecret;

    public bool IsNonSecretPlainTextEditorVisible => !IsBoolEditorVisible && !IsSecret;

    [ObservableProperty]
    private string _value = string.Empty;

    public bool BoolValue
    {
        get => bool.TryParse(Value, out var v) && v;
        set
        {
            var s = value ? "True" : "False";
            if (Value == s) return;
            Value = s;
        }
    }

    public PluginPropertyViewModel(PluginPropertyInfo info, string initialValue)
    {
        PropertyName = info.PropertyName;
        Label = info.Label;
        Description = info.Description;
        IsSecret = info.IsSecret;
        PropertyType = info.PropertyType;
        _value = initialValue;
    }

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(BoolValue));
}

public partial class PluginSettingsViewModel : ObservableObject
{
    private readonly string _pluginName;
    private readonly SettingsManager _settingsManager;
    private readonly AgentService? _agentService;

    public string PluginName => _pluginName;
    public ObservableCollection<PluginPropertyViewModel> Properties { get; } = new();

    public PluginSettingsViewModel(PluginSettingInfo info, SettingsManager settingsManager, Dictionary<string, string> currentValues, AgentService? agentService = null)
    {
        _pluginName = info.PluginName;
        _settingsManager = settingsManager;
        _agentService = agentService;

        foreach (var prop in info.Properties)
        {
            currentValues.TryGetValue(prop.PropertyName, out var val);
            Properties.Add(new PluginPropertyViewModel(prop, val ?? string.Empty));
        }
    }

    public void FlushSave()
    {
        var settings = _settingsManager.Load();
        if (!settings.PluginSettings.TryGetValue(_pluginName, out var pluginDict))
        {
            pluginDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings.PluginSettings[_pluginName] = pluginDict;
        }

        foreach (var prop in Properties)
        {
            pluginDict[prop.PropertyName] = prop.Value;
        }

        _settingsManager.Save(settings);
        _agentService?.DispatchPluginSettingsLoaded(_pluginName);
    }
}
