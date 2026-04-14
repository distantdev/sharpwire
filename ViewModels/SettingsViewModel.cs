using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sharpwire.Core;
using Sharpwire.Core.Agents;
using Sharpwire.Core.MetaToolbox;
using Sharpwire.Core.Secrets;
using Sharpwire.Core.Update;

namespace Sharpwire.ViewModels;

public partial class PluginSettingsListRowViewModel : ObservableObject
{
    private readonly Action<PluginSettingInfo> _onEdit;

    public PluginSettingInfo Definition { get; }

    public string PluginName => Definition.PluginName;

    public string SettingSummary => Definition.Properties.Count == 1 ? "1 setting" : $"{Definition.Properties.Count} settings";

    public PluginSettingsListRowViewModel(PluginSettingInfo definition, Action<PluginSettingInfo> onEdit)
    {
        Definition = definition;
        _onEdit = onEdit;
    }

    [RelayCommand]
    private void Edit() => _onEdit(Definition);
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsManager _settingsManager;
    private readonly LlmApiKeyStore _apiKeyStore;
    private readonly AgentService _agentService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly Action<PluginSettingInfo>? _openPluginSettingsTab;

    [ObservableProperty]
    private string _googleApiKey = string.Empty;

    [ObservableProperty]
    private string _openAiApiKey = string.Empty;

    [ObservableProperty]
    private string _anthropicApiKey = string.Empty;

    [ObservableProperty]
    private bool _allowOrchestratorAgentCreation;

    [ObservableProperty]
    private bool _enableCoder = true;

    [ObservableProperty]
    private bool _enableReviewer = true;

    /// <summary>0 = unlimited return traversals before HITL.</summary>
    [ObservableProperty]
    private int _maxLoopIterations = 3;

    [ObservableProperty]
    private string _llmProvider = "Gemini";

    [ObservableProperty]
    private string _modelId = "gemini-2.0-flash";

    public ObservableCollection<string> GeminiModels { get; } = new();
    public ObservableCollection<string> OpenAiModels { get; } = new();
    public ObservableCollection<string> ClaudeModels { get; } = new();

    [ObservableProperty]
    private string _customEndpoint = string.Empty;

    [ObservableProperty]
    private string _geminiModelId = "gemini-2.0-flash";

    [ObservableProperty]
    private bool _enableAutoUpdateChecks = true;

    [ObservableProperty]
    private bool _isUpdateBusy;

    [ObservableProperty]
    private string _updateStatus = "Not checked yet.";

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _designReviewResult = string.Empty;

    private bool _hydrating;

    public ObservableCollection<PluginSettingsListRowViewModel> PluginSettingRows { get; } = new();

    public bool ShowPluginSettingsSection => _openPluginSettingsTab != null;

    public bool ShowNoPluginSettingsHint => ShowPluginSettingsSection && PluginSettingRows.Count == 0;

    public SettingsViewModel(SettingsManager settingsManager, LlmApiKeyStore apiKeyStore, AgentService agentService, IAppUpdateService appUpdateService, Action<PluginSettingInfo>? openPluginSettingsTab = null)
    {
        _settingsManager = settingsManager;
        _apiKeyStore = apiKeyStore;
        _agentService = agentService;
        _appUpdateService = appUpdateService;
        _openPluginSettingsTab = openPluginSettingsTab;

        _agentService.OnModelsUpdated += HandleModelsUpdated;
        RefreshModelLists();

        var settings = _settingsManager.Load(_apiKeyStore);
        _hydrating = true;
        GoogleApiKey = _apiKeyStore.GetGoogleApiKey() ?? string.Empty;
        OpenAiApiKey = _apiKeyStore.GetOpenAiApiKey() ?? string.Empty;
        AnthropicApiKey = _apiKeyStore.GetAnthropicApiKey() ?? string.Empty;
        AllowOrchestratorAgentCreation = settings.AllowOrchestratorAgentCreation;
        EnableCoder = settings.EnableCoder;
        EnableReviewer = settings.EnableReviewer;
        MaxLoopIterations = settings.MaxLoopIterations;
        
        LlmProvider = string.IsNullOrWhiteSpace(settings.LlmProvider) ? "Gemini" : settings.LlmProvider;
        CustomEndpoint = settings.CustomEndpoint ?? string.Empty;
        
        // Use ModelId if set, otherwise fallback to GeminiModelId for migration
        ModelId = !string.IsNullOrWhiteSpace(settings.ModelId) ? settings.ModelId : settings.GeminiModelId;
        GeminiModelId = ModelId; // Keep legacy field in sync for now
        EnableAutoUpdateChecks = settings.EnableAutoUpdateChecks;

        _hydrating = false;

        if (_openPluginSettingsTab != null)
        {
            foreach (var def in _agentService.PluginSettingsDefinitions.OrderBy(x => x.PluginName, StringComparer.OrdinalIgnoreCase))
                PluginSettingRows.Add(new PluginSettingsListRowViewModel(def, _openPluginSettingsTab));
        }

        if (EnableAutoUpdateChecks)
            _ = CheckForUpdatesAsync();
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
            _hydrating = true; // Use hydrating flag to prevent saving while updating list
            
            if (provider == "Gemini") UpdateCollection(GeminiModels, models);
            else if (provider == "OpenAI") UpdateCollection(OpenAiModels, models);
            else if (provider == "Anthropic") UpdateCollection(ClaudeModels, models);

            ModelId = currentModel;
            _hydrating = false;
        });
    }

    private void UpdateCollection(ObservableCollection<string> collection, List<string> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    public void PersistNonSecretSettings()
    {
        var current = _settingsManager.Load(_apiKeyStore);
        current.AllowOrchestratorAgentCreation = AllowOrchestratorAgentCreation;
        current.EnableCoder = EnableCoder;
        current.EnableReviewer = EnableReviewer;
        current.MaxLoopIterations = MaxLoopIterations;
        current.LlmProvider = LlmProvider;
        current.ModelId = string.IsNullOrWhiteSpace(ModelId) ? string.Empty : ModelId.Trim();
        current.CustomEndpoint = CustomEndpoint?.Trim() ?? string.Empty;
        current.GeminiModelId = ModelId;
        current.EnableAutoUpdateChecks = EnableAutoUpdateChecks;
        _settingsManager.Save(current);
    }

    partial void OnGoogleApiKeyChanged(string value)
    {
        if (!_hydrating)
        {
            try
            {
                _apiKeyStore.SetGoogleApiKey(value);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save Google API key: {ex.Message}");
            }
        }
    }

    partial void OnOpenAiApiKeyChanged(string value)
    {
        if (!_hydrating)
        {
            try
            {
                _apiKeyStore.SetOpenAiApiKey(value);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save OpenAI API key: {ex.Message}");
            }
        }
    }

    partial void OnAnthropicApiKeyChanged(string value)
    {
        if (!_hydrating)
        {
            try
            {
                _apiKeyStore.SetAnthropicApiKey(value);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save Anthropic API key: {ex.Message}");
            }
        }
    }

    partial void OnLlmProviderChanged(string value)
    {
        if (!_hydrating) PersistNonSecretSettings();
    }

    partial void OnModelIdChanged(string value)
    {
        if (!_hydrating) PersistNonSecretSettings();
    }

    partial void OnCustomEndpointChanged(string value)
    {
        if (!_hydrating) PersistNonSecretSettings();
    }

    partial void OnAllowOrchestratorAgentCreationChanged(bool value)
    {
        if (!_hydrating) PersistNonSecretSettings();
    }

    partial void OnMaxLoopIterationsChanged(int value)
    {
        if (!_hydrating) PersistNonSecretSettings();
    }

    partial void OnEnableCoderChanged(bool value)
    {
        if (!_hydrating) PersistNonSecretSettings();
    }

    partial void OnEnableReviewerChanged(bool value)
    {
        if (!_hydrating) PersistNonSecretSettings();
    }

    partial void OnGeminiModelIdChanged(string value)
    {
        if (!_hydrating) PersistNonSecretSettings();
    }

    partial void OnEnableAutoUpdateChecksChanged(bool value)
    {
        if (!_hydrating) PersistNonSecretSettings();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsUpdateBusy)
            return;
        IsUpdateBusy = true;
        try
        {
            var result = await _appUpdateService.CheckForUpdatesAsync(CancellationToken.None).ConfigureAwait(true);
            IsUpdateAvailable = result.IsUpdateAvailable;
            UpdateStatus = result.Message;
        }
        catch (Exception ex)
        {
            IsUpdateAvailable = false;
            UpdateStatus = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (IsUpdateBusy)
            return;
        IsUpdateBusy = true;
        try
        {
            var result = await _appUpdateService.ApplyUpdatesAsync(CancellationToken.None).ConfigureAwait(true);
            UpdateStatus = result.Message;
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update apply failed: {ex.Message}";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunDesignReviewAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        DesignReviewResult = string.Empty;
        try
        {
            var payload = _agentService.BuildAmbientDesignReviewPayload();
            DesignReviewResult = await _agentService.ReviewProposedSharpwireDesignAsync(payload, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DesignReviewResult = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
