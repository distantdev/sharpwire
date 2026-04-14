using System;
using System.Collections.Generic;

namespace Sharpwire.Core.MetaToolbox;

[AttributeUsage(AttributeTargets.Class)]
public class PluginSettingsAttribute : Attribute
{
    public string PluginName { get; }
    public PluginSettingsAttribute(string pluginName) => PluginName = pluginName;
}

[AttributeUsage(AttributeTargets.Property)]
public class PluginSettingAttribute : Attribute
{
    public string Label { get; }
    public string Description { get; }
    public bool IsSecret { get; }
    
    public PluginSettingAttribute(string label, string description = "", bool isSecret = false)
    {
        Label = label;
        Description = description;
        IsSecret = isSecret;
    }
}

/// <summary>Implement on the tool class so the host reuses one instance for tools and settings notifications.</summary>
public interface IPluginWithSettings
{
    void OnSettingsLoaded(Dictionary<string, object> settings);
}
