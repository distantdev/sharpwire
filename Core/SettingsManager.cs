using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sharpwire.Core.Secrets;

namespace Sharpwire.Core;

public class AppSettings
{
    public bool AllowOrchestratorAgentCreation { get; set; } = true;
    public int MaxLoopIterations { get; set; } = 3;
    public Dictionary<string, Dictionary<string, string>> PluginSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool EnableCoder { get; set; } = true;
    public bool EnableReviewer { get; set; } = true;
    public const string DefaultLlmProvider = "Gemini";
    public const string DefaultModelId = "gemini-2.5-flash";
    public const string DefaultCustomEndpoint = "http://localhost:11434/v1";
    public string LlmProvider { get; set; } = DefaultLlmProvider;
    public string ModelId { get; set; } = DefaultModelId;
    public string CustomEndpoint { get; set; } = string.Empty;
    public string GeminiModelId { get; set; } = DefaultModelId;
    public bool EnableAutoUpdateChecks { get; set; } = true;
}

public class SettingsManager
{
    private readonly string _settingsFilePath;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public SettingsManager(string workspacePath)
    {
        _settingsFilePath = Path.Combine(workspacePath, ".sharpwire", "settings.json");
        var dir = Path.GetDirectoryName(_settingsFilePath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    public AppSettings Load(LlmApiKeyStore? apiKeyStore = null)
    {
        if (File.Exists(_settingsFilePath) && apiKeyStore != null)
            LlmApiKeyStore.TryMigrateKeyFromSettingsJson(_settingsFilePath, apiKeyStore);

        if (!File.Exists(_settingsFilePath)) return new AppSettings();

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, WriteOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }
}
