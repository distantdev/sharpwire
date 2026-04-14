using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Anthropic;
using Sharpwire.Core.Plugins;
using Sharpwire.Core.Secrets;
using Sharpwire.Core.Session;
using Sharpwire.Core.Security;
using Sharpwire.Core.Tools;
using Sharpwire.Core.Yaml;
using Sharpwire.ViewModels;
using Sharpwire.Core;
using Sharpwire.Core.Workflow;
using Sharpwire.Core.MetaToolbox;
using Sharpwire.Core.Hooks;

namespace Sharpwire.Core.Agents;

public class AgentDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Instructions { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string NextAgentName { get; init; } = AgentService.OrchestratorAgentName;
    public List<string> EnabledTools { get; init; } = new();
    /// <summary>Hex color (e.g. <c>#0D7377</c>) for chat bubble and graph node header.</summary>
    public string AccentColor { get; init; } = "#0D7377";
    /// <summary>Orchestrator entry point for a wired chain (STANDARDS / MAF).</summary>
    public bool IsChainEntry { get; set; }
    /// <summary>Optional JSON Schema for enforcing structured response (MAF 1.0 ResponseFormat).</summary>
    public string? JsonSchema { get; set; }

    /// <summary>The LLM provider for this agent (Gemini, OpenAI, Custom/Local). If null/empty, uses global default.</summary>
    public string? LlmProvider { get; set; }
    /// <summary>Model ID for this agent. If null/empty, uses global default.</summary>
    public string? ModelId { get; set; }
}

public class Agent
{
    private readonly IChatClient _client;
    public AgentDefinition Definition { get; }
    public string DynamicInstructions { get; set; } = string.Empty;
    public List<AITool> Tools { get; } = new List<AITool>();

    public Agent(IChatClient client, AgentDefinition definition)
    {
        _client = client;
        Definition = definition;
        DynamicInstructions = definition.Instructions;
    }

    public async Task<ChatResponse> GetResponseAsync(
        string input,
        List<ChatMessage> history,
        CancellationToken ct,
        Action<string>? onStreamText = null,
        bool appendReviewerInstruction = false)
    {
        var messages = BuildMessages(input, history, appendReviewerInstruction);
        var tools = new List<AITool>(Tools);

        if (appendReviewerInstruction)
        {
            // Inject a local "ApproveWork" tool that signals success without magic strings.
            var approveTool = AIFunctionFactory.Create(() => "Success signaled.", "ApproveWork", "Call this tool if the task is completed correctly and you are satisfied with the result.");
            tools.Add(approveTool);
        }

        var options = new ChatOptions { Tools = tools };

        // Debug: Log tool availability
        if (Tools.Count > 0)
        {
            var toolList = string.Join(", ", Tools.Select(t => t.Name));
            // Note: We can't call SendLogLine here because it's in the other partial class or private. 
            // But we can use a trick or just assume we'll add a helper.
            // For now, let's just make sure we aren't losing tools.
        }

        if (!string.IsNullOrWhiteSpace(Definition.JsonSchema))
        {
            options.ResponseFormat = ChatResponseFormat.Json;
            try
            {
                var schemaProp = typeof(ChatResponseFormat).GetProperty("JsonSchema");
                if (schemaProp != null)
                {
                    schemaProp.SetValue(options.ResponseFormat, Definition.JsonSchema);
                }
            }
            catch { /* fallback */ }
        }

        if (onStreamText != null)
        {
            try
            {
                var collected = new List<ChatResponseUpdate>();
                var streamedAny = false;
                await foreach (var update in _client.GetStreamingResponseAsync(messages, options, ct))
                {
                    collected.Add(update);
                    var piece = ChatMessageTextExtractor.GetCombinedText(update);
                    if (!string.IsNullOrEmpty(piece))
                    {
                        onStreamText(piece);
                        streamedAny = true;
                    }
                }

                ChatResponse final;
                if (collected.Count == 0)
                    final = await _client.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
                else
                    final = collected.ToChatResponse();

                if (!streamedAny)
                {
                    var forUi = !string.IsNullOrEmpty(final.Text)
                        ? final.Text
                        : LastNonEmptyAssistantTextFromResponse(final);
                    if (!string.IsNullOrEmpty(forUi))
                        onStreamText(forUi);
                }

                return final;
            }
            catch (NotImplementedException)
            {
                var fallback = await _client.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
                var forUi = !string.IsNullOrEmpty(fallback.Text)
                    ? fallback.Text
                    : LastNonEmptyAssistantTextFromResponse(fallback);
                if (!string.IsNullOrEmpty(forUi))
                    onStreamText(forUi);
                return fallback;
            }
        }

        return await _client.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
    }

    /// <summary>Assistant-visible text for UI streaming when <see cref="ChatResponse.Text"/> is empty.</summary>
    private static string LastNonEmptyAssistantTextFromResponse(ChatResponse response)
    {
        foreach (var m in response.Messages.Reverse())
        {
            if (m.Role != ChatRole.Assistant)
                continue;
            var combined = ChatMessageTextExtractor.GetCombinedText(m);
            var t = ChatTextNormalizer.ForDisplay(combined);
            if (!string.IsNullOrWhiteSpace(t))
                return t;
        }

        return string.Empty;
    }

    private List<ChatMessage> BuildMessages(string input, List<ChatMessage> history, bool appendReviewerInstruction)
    {
        var messages = new List<ChatMessage>();
        var instructions = !string.IsNullOrEmpty(DynamicInstructions) ? DynamicInstructions : Definition.Instructions;

        if (appendReviewerInstruction)
        {
            instructions += "\n\nIMPORTANT: If the task is fully and correctly completed, you MUST call the 'ApproveWork' tool to signal success.";
        }

        if (!string.IsNullOrEmpty(instructions))
            messages.Add(new ChatMessage(ChatRole.System, instructions));
        messages.AddRange(history);
        messages.Add(new ChatMessage(ChatRole.User, input));
        return messages;
    }
}

public partial class AgentService
{
    public const string OrchestratorAgentName = "Orchestrator";
    public const string DelegateToAgentToolId = "DelegateToAgent";
    public const string CreateNewAgentToolId = "CreateNewAgent";
    public const string CreateAgentChainToolId = "CreateAgentChain";
    public const string ReviewProposedSharpwireDesignToolId = "ReviewProposedSharpwireDesign";
    public const string AskSharpwireDocumentationToolId = "AskSharpwireDocumentation";
    private static readonly string[] RecoveryToolIds = { "ReadFile", "ListFiles", "WriteFile" };
    private static readonly string[] AgentOnlyFileToolIds = { "ReadFile", "ListFiles", "WriteFile" };
    private const int ModelStreamBatchMs = 75;
    private readonly object _streamBatchLock = new();
    private readonly Dictionary<string, StringBuilder> _streamBatchBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _streamBatchLastEmitTicks = new(StringComparer.OrdinalIgnoreCase);
    
    public Dictionary<string, Agent> Agents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AITool> AvailableTools { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PluginSettingInfo> PluginSettingsDefinitions { get; private set; } = new();

    public event Action<string>? OnLog;
    public event Action<MessageRole, string, string, string?>? OnChatMessage;
    public event Action<string>? OnAgentAppearanceChanged;
    public event Action<string, AgentState>? OnAgentStateChanged;
    public event Action<string, AgentActivityKind>? OnAgentActivityChanged;
    /// <summary>Raised when a model call starts streaming for that agent (accent hex).</summary>
    public event Action<string, string?>? OnAgentModelStreamStarted;
    /// <summary>Raised for each text chunk from the streaming model for that agent.</summary>
    public event Action<string, string>? OnAgentModelStreamDelta;
    /// <summary>Raised when the streaming response for that agent is complete (or falling back to non-streamed).</summary>
    public event Action<string>? OnAgentModelStreamEnded;
    public event Action<Agent>? OnAgentAdded;
    public event Action? OnAgentsReady;
    /// <summary>Fired when an individual provider's connection status changes (ProviderName, IsConnected, StatusText).</summary>
    public event Action<string, bool, string>? OnProviderStatusChanged;
    /// <summary>Fired when a user agent is renamed (<paramref name="oldName"/> → <paramref name="newName"/>). UI should retitle tabs and graph nodes.</summary>
    public event Action<string, string>? OnAgentRenamed;

    /// <summary>Fired when the orchestrator's live system prompt (<see cref="Agent.DynamicInstructions"/>) is rebuilt — e.g. agent added/removed/renamed.</summary>
    public event Action<string>? OnOrchestratorPromptUpdated;

    /// <summary>Wired handoff (Nodify / StateMap) changed — refresh connection visuals.</summary>
    public event Action? OnHandoffTopologyChanged;

    /// <summary>Fired when model lists are dynamically fetched from providers.</summary>
    public event Action<string, List<string>>? OnModelsUpdated;

    /// <summary>Fired when a scene is loaded, signaling the UI to refresh the entire graph.</summary>
    public event Action? OnSceneLoaded;

    /// <summary>Fired when plugin compilation starts, succeeds, or fails (IsCompiling, ErrorMessage).</summary>
    public event Action<bool, string?>? OnPluginStatusUpdated;

    public string? LastPluginError { get; private set; }

    /// <summary>Runtime handoff step in <see cref="ExecuteAgentAsync"/> (<paramref name="fromAgent"/> → <paramref name="toAgent"/>). Graph wire pulse only; not chat or @mention traffic. Third parameter is true if return wire.</summary>
    public event Action<string, string, bool>? OnAgentHandoffFlow;

    public List<string> GeminiModels { get; private set; } = new();
    public List<string> OpenAiModels { get; private set; } = new();
    public List<string> ClaudeModels { get; private set; } = new();

    private IChatClient? _chatClient;

    private readonly string _workspacePath;
    private readonly SettingsManager _settingsManager;
    private readonly LlmApiKeyStore _apiKeyStore;
    private readonly AgentSession _session;
    private readonly ToolApprovalCoordinator _toolApproval;
    private readonly PluginCompilerService _pluginCompiler;
    private LifecycleHookPipeline _lifecycleHookPipeline = LifecycleHookPipeline.Empty;

    private readonly object _pluginSettingsDispatchLock = new();
    private Assembly? _dynamicPluginsAssembly;
    private Dictionary<Type, object> _dynamicPluginInstances = new();

    /// <summary>Live model stream buffers per agent; create and mutate only on the UI thread.</summary>
    private readonly Dictionary<string, AgentModelStreamMonitor> _modelStreamMonitors = new(StringComparer.OrdinalIgnoreCase);

    public AgentService(AppPaths paths, SettingsManager settingsManager, LlmApiKeyStore apiKeyStore, AgentSession session, ToolApprovalCoordinator toolApproval, PluginCompilerService pluginCompiler)
    {
        _workspacePath = paths.WorkspaceDirectory;
        _settingsManager = settingsManager;
        _apiKeyStore = apiKeyStore;
        _session = session;
        _toolApproval = toolApproval;
        _pluginCompiler = pluginCompiler;
        _toolApproval.LogHookWarning = SendLogLine;
        PluginChat.SetSystemEmitter((text, source) =>
        {
            var sender = string.IsNullOrWhiteSpace(source) ? "Plugin" : $"Plugin:{source}";
            EmitChatMessage(MessageRole.System, text, sender, null);
            return true;
        });
    }

    private void BeginModelStreamBatch(string agentName)
    {
        lock (_streamBatchLock)
        {
            _streamBatchBuffers[agentName] = new StringBuilder();
            _streamBatchLastEmitTicks[agentName] = DateTime.UtcNow.Ticks;
        }
    }

    private void AppendModelStreamBatch(string agentName, string piece)
    {
        if (string.IsNullOrEmpty(piece))
            return;
        lock (_streamBatchLock)
        {
            if (!_streamBatchBuffers.TryGetValue(agentName, out var sb))
            {
                sb = new StringBuilder();
                _streamBatchBuffers[agentName] = sb;
                _streamBatchLastEmitTicks[agentName] = DateTime.UtcNow.Ticks;
            }

            sb.Append(piece);
            var now = DateTime.UtcNow.Ticks;
            var last = _streamBatchLastEmitTicks[agentName];
            if ((now - last) / TimeSpan.TicksPerMillisecond < ModelStreamBatchMs)
                return;

            _streamBatchLastEmitTicks[agentName] = now;
            if (sb.Length == 0)
                return;
            var text = sb.ToString();
            sb.Clear();
            OnAgentModelStreamDelta?.Invoke(agentName, text);
        }
    }

    private void FlushModelStreamBatch(string agentName)
    {
        lock (_streamBatchLock)
        {
            if (_streamBatchBuffers.TryGetValue(agentName, out var sb) && sb.Length > 0)
                OnAgentModelStreamDelta?.Invoke(agentName, sb.ToString());
            _streamBatchBuffers.Remove(agentName);
            _streamBatchLastEmitTicks.Remove(agentName);
        }
    }

    /// <summary>Core agents recreated from settings / code each run; their definitions are not persisted or user-editable.</summary>
    public static bool IsBuiltInAgent(string name) =>
        name.Equals(OrchestratorAgentName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Host-only agent name; not persisted, not shown on the graph, invoked programmatically for design review.</summary>
    public const string SharpwireDesignReviewAgentName = "__SharpwireDesignReview";

    public static bool IsHiddenSystemAgent(string name) =>
        name.Equals(SharpwireDesignReviewAgentName, StringComparison.OrdinalIgnoreCase);

    public string MakeUniqueAgentName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Agent";
        var name = baseName;
        for (var n = 2; Agents.ContainsKey(name); n++)
            name = $"{baseName}_{n}";
        return name;
    }

    public string PickNextAvailableAccentColor()
    {
        var used = Agents.Values.Select(a => a.Definition.AccentColor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in AgentAccentOptions.Presets)
        {
            if (!used.Contains(preset.Hex))
                return preset.Hex;
        }
        return AgentAccentOptions.Presets[Agents.Count % AgentAccentOptions.Presets.Count].Hex;
    }

    private void SendLogLine(string message) =>
        OnLog?.Invoke(LogRedaction.MaskForUi(message));

    public void Log(string message) => SendLogLine(message);

    internal async Task RunLifecycleHooksAsync(LifecycleHookContext context, CancellationToken ct)
    {
        if (!_lifecycleHookPipeline.HasHooks)
            return;

        try
        {
            await _lifecycleHookPipeline.ExecuteAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SendLogLine($"Hook warning ({context.Stage}): {ex.Message}");
        }
    }

    /// <summary>Delivers persisted plugin settings to <see cref="IPluginWithSettings"/> (after compile or when the user saves the plugin settings tab).</summary>
    public void DispatchPluginSettingsLoaded(string pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return;

        lock (_pluginSettingsDispatchLock)
        {
            var def = PluginSettingsDefinitions.FirstOrDefault(p =>
                string.Equals(p.PluginName, pluginName, StringComparison.OrdinalIgnoreCase));
            if (def == null || _dynamicPluginsAssembly == null)
                return;

            var type = ResolvePluginType(_dynamicPluginsAssembly, def.TypeName);
            if (type == null)
            {
                SendLogLine($"Plugin settings: could not resolve type '{def.TypeName}' for '{pluginName}'.");
                return;
            }

            if (!typeof(IPluginWithSettings).IsAssignableFrom(type))
                return;

            if (!_dynamicPluginInstances.TryGetValue(type, out var instance))
            {
                try
                {
                    instance = Activator.CreateInstance(type);
                    if (instance != null)
                        _dynamicPluginInstances[type] = instance;
                }
                catch (Exception ex)
                {
                    SendLogLine($"Plugin settings: could not create instance of '{type.Name}' for '{pluginName}': {ex.Message}");
                    return;
                }
            }

            if (instance is not IPluginWithSettings receiver)
                return;

            var appSettings = _settingsManager.Load(_apiKeyStore);
            appSettings.PluginSettings.TryGetValue(pluginName, out var raw);
            var payload = PluginSettingsPayloadBuilder.Build(def, raw);
            PluginSettingsPayloadBuilder.ApplyPayloadToInstance(instance, def, payload);

            try
            {
                receiver.OnSettingsLoaded(payload);
            }
            catch (Exception ex)
            {
                SendLogLine($"Plugin '{pluginName}' OnSettingsLoaded failed: {ex.Message}");
            }
        }
    }

    private static Type? ResolvePluginType(Assembly assembly, string typeName)
    {
        var t = assembly.GetType(typeName);
        if (t != null)
            return t;
        try
        {
            return assembly.GetExportedTypes().FirstOrDefault(x =>
                string.Equals(x.FullName, typeName, StringComparison.Ordinal) ||
                string.Equals(x.Name, typeName, StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }

    private void EmitChatMessage(MessageRole role, string text, string sender, string? accent)
    {
        var t = ChatTextNormalizer.ForDisplay(LogRedaction.MaskForUi(text));
        if (role == MessageRole.Agent && string.IsNullOrWhiteSpace(t))
            return;
        OnChatMessage?.Invoke(role, t, sender, accent);
    }

    public void RaiseAgentActivity(string agentName, AgentActivityKind activity) =>
        OnAgentActivityChanged?.Invoke(agentName, activity);

    /// <summary>Last assistant string with visible content (after stripping zero-width / format chars).</summary>
    private static string LastNonEmptyAssistantText(ChatResponse response)
    {
        foreach (var m in response.Messages.Reverse())
        {
            if (m.Role != ChatRole.Assistant)
                continue;
            var combined = ChatMessageTextExtractor.GetCombinedText(m);
            var t = ChatTextNormalizer.ForDisplay(combined);
            if (!string.IsNullOrWhiteSpace(t))
                return t;
        }

        return string.Empty;
    }

    /// <summary>Returns the shared stream monitor for <paramref name="agentName"/> (creates if needed). Call from the UI thread only.</summary>
    public AgentModelStreamMonitor GetOrCreateModelStreamMonitor(string agentName)
    {
        if (!_modelStreamMonitors.TryGetValue(agentName, out var m))
        {
            m = new AgentModelStreamMonitor();
            _modelStreamMonitors[agentName] = m;
        }

        return m;
    }

    /// <summary>Moves the stream buffer key after a rename. Call from the UI thread only.</summary>
    public void RenameModelStreamMonitorKey(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            return;
        if (!_modelStreamMonitors.Remove(oldName, out var m))
            return;
        _modelStreamMonitors[newName] = m;
    }

    /// <summary>Discards buffered stream output for a removed agent.</summary>
    public void RemoveModelStreamMonitor(string agentName) =>
        _modelStreamMonitors.Remove(agentName);

    private static bool ResponseUsedTools(ChatResponse response)
    {
        foreach (var m in response.Messages)
        {
            if (m.Role == ChatRole.Tool)
                return true;
            foreach (var c in m.Contents)
            {
                if (c is FunctionCallContent)
                    return true;
            }
        }

        return false;
    }

    private static bool TryDetectAgentOnlyToolNotFound(string? text, out string toolId)
    {
        toolId = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var candidate in AgentOnlyFileToolIds)
        {
            if (!text.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                continue;
            if (text.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || text.Contains("tool not found", StringComparison.OrdinalIgnoreCase))
            {
                toolId = candidate;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeAccentHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return "#0D7377";
        var s = hex.Trim();
        if (!s.StartsWith('#'))
            s = "#" + s;
        return s.Length is 7 or 9 && s[1..].Length is 6 or 8
            ? s
            : "#0D7377";
    }

    private static AgentDefinition CloneDefinition(AgentDefinition d, string accentColor) =>
        new()
        {
            Name = d.Name,
            Role = d.Role,
            Description = d.Description,
            Instructions = d.Instructions,
            NextAgentName = d.NextAgentName,
            EnabledTools = d.EnabledTools.ToList(),
            AccentColor = accentColor,
            IsChainEntry = d.IsChainEntry,
            JsonSchema = d.JsonSchema,
            LlmProvider = d.LlmProvider,
            ModelId = d.ModelId
        };

    private AgentDefinition CloneDefinitionWithFilteredTools(AgentDefinition d)
    {
        var tools = (d.EnabledTools ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id) && AvailableTools.ContainsKey(id.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AgentDefinition
        {
            Name = d.Name,
            Role = d.Role,
            Description = d.Description,
            Instructions = d.Instructions,
            NextAgentName = d.NextAgentName,
            EnabledTools = tools,
            AccentColor = d.AccentColor,
            IsChainEntry = d.IsChainEntry,
            JsonSchema = d.JsonSchema,
            LlmProvider = d.LlmProvider,
            ModelId = d.ModelId
        };
    }

    private AgentDefinition EnsureRecoveryTools(AgentDefinition d)
    {
        var enabled = (d.EnabledTools ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasUsableTool = enabled.Any(id => AvailableTools.ContainsKey(id));
        if (hasUsableTool)
            return d;

        var recovery = RecoveryToolIds
            .Where(id => AvailableTools.ContainsKey(id))
            .ToList();
        if (recovery.Count == 0)
            return d;

        foreach (var id in recovery)
        {
            if (!enabled.Contains(id, StringComparer.OrdinalIgnoreCase))
                enabled.Add(id);
        }

        return new AgentDefinition
        {
            Name = d.Name,
            Role = d.Role,
            Description = d.Description,
            Instructions = d.Instructions,
            NextAgentName = d.NextAgentName,
            EnabledTools = enabled,
            AccentColor = d.AccentColor,
            IsChainEntry = d.IsChainEntry,
            JsonSchema = d.JsonSchema,
            LlmProvider = d.LlmProvider,
            ModelId = d.ModelId
        };
    }

    private static bool EnabledToolSetsDiffer(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        var sa = new HashSet<string>(a ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var sb = new HashSet<string>(b ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        return !sa.SetEquals(sb);
    }

    /// <summary>Updates accent for any agent; persists <c>agents.json</c>.</summary>
    public void SetAgentAccent(string name, string accentHex)
    {
        if (_chatClient == null || !Agents.TryGetValue(name, out var agent) || IsHiddenSystemAgent(name))
            return;

        var hex = NormalizeAccentHex(accentHex);
        var newDef = CloneDefinition(agent.Definition, hex);
        var rebuilt = CreateAgentWithTools(newDef);
        Agents[name] = rebuilt;
        if (name.Equals(OrchestratorAgentName, StringComparison.OrdinalIgnoreCase))
            UpdateOrchestratorPrompt(rebuilt);

        SaveAgentDefinitions();

        OnAgentAppearanceChanged?.Invoke(name);
    }

    private string? AccentForSender(string senderName) =>
        Agents.TryGetValue(senderName, out var a) ? a.Definition.AccentColor : null;

    private Agent CreateAgentWithTools(AgentDefinition definition)
    {
        var client = ResolveChatClient(definition);
        var agent = new Agent(client, definition);
        foreach (var toolId in definition.EnabledTools)
        {
            if (AvailableTools.TryGetValue(toolId, out var tool))
                agent.Tools.Add(tool);
        }
        return agent;
    }

    public void AddAgent(AgentDefinition definition, bool isInitialLoad = false, string? renamedFromKey = null)
    {
        if (_chatClient == null) throw new InvalidOperationException("ChatClient not initialized.");

        var name = (definition.Name ?? string.Empty).Trim();
        var next = (definition.NextAgentName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(next)) next = OrchestratorAgentName;

        if (IsHiddenSystemAgent(name) && !isInitialLoad)
        {
            SendLogLine($"System: Agent name '{SharpwireDesignReviewAgentName}' is reserved for the host.");
            return;
        }

        var color = definition.AccentColor;
        if (color == "#0D7377" && !isInitialLoad && !Agents.ContainsKey(name))
        {
            color = PickNextAvailableAccentColor();
        }

        var trimmedDef = new AgentDefinition
        {
            Name = name,
            Role = (definition.Role ?? string.Empty).Trim(),
            Description = (definition.Description ?? string.Empty).Trim(),
            Instructions = definition.Instructions,
            NextAgentName = next,
            EnabledTools = definition.EnabledTools,
            AccentColor = color,
            IsChainEntry = definition.IsChainEntry,
            JsonSchema = definition.JsonSchema,
            LlmProvider = definition.LlmProvider,
            ModelId = definition.ModelId
        };
        
        bool isUpdate = Agents.ContainsKey(name);
        string? priorNext = null;
        if (isUpdate && Agents.TryGetValue(name, out var priorAgent))
            priorNext = priorAgent.Definition.NextAgentName;

        var agent = CreateAgentWithTools(trimmedDef);

        Agents[name] = agent;
        
        // Update Orchestrator prompt with new agent
        if (Agents.TryGetValue(OrchestratorAgentName, out var orchestrator))
        {
            UpdateOrchestratorPrompt(orchestrator);
        }

        if (!isInitialLoad)
        {
            if (renamedFromKey != null)
            {
                _session.State.RenameHandoffOverrideKey(renamedFromKey, name);
                _session.State.RenameHandoffTarget(renamedFromKey, name);
                RenameWorkflowEdgeEndpoints(renamedFromKey, name);
                OnHandoffTopologyChanged?.Invoke();
            }

            SaveAgentDefinitions();

            var nextChanged = isUpdate
                && !string.Equals(priorNext ?? string.Empty, next, StringComparison.OrdinalIgnoreCase);
            var newAgentWithHandoff = !isUpdate
                && !next.Equals(OrchestratorAgentName, StringComparison.OrdinalIgnoreCase)
                && Agents.ContainsKey(next);
            if (nextChanged || newAgentWithHandoff)
                EnsureWorkflowEdgesMaterialized();
            
            var treatAsNewGraphNode = !isUpdate && name != OrchestratorAgentName && renamedFromKey == null;
            if (treatAsNewGraphNode)
            {
                OnAgentAdded?.Invoke(agent);
            }
            else
            {
                OnAgentAppearanceChanged?.Invoke(name);
                if (renamedFromKey != null)
                    OnAgentRenamed?.Invoke(renamedFromKey, name);
            }
            
            SendLogLine($"System: Agent '{name}' updated.");
        }
    }

    private static AgentDefinition CloneDefinitionWithNext(AgentDefinition d, string nextAgentName) =>
        new()
        {
            Name = d.Name,
            Role = d.Role,
            Description = d.Description,
            Instructions = d.Instructions,
            NextAgentName = nextAgentName,
            EnabledTools = d.EnabledTools.ToList(),
            AccentColor = d.AccentColor,
            IsChainEntry = d.IsChainEntry,
            JsonSchema = d.JsonSchema,
            LlmProvider = d.LlmProvider,
            ModelId = d.ModelId
        };

    public void UpdateAgent(string currentKey, AgentDefinition newDefinition)
    {
        if (_chatClient == null || !Agents.ContainsKey(currentKey) || IsBuiltInAgent(currentKey) || IsHiddenSystemAgent(currentKey))
            return;

        var newName = (newDefinition.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            SendLogLine("System: Agent name cannot be empty.");
            return;
        }

        if (currentKey.Equals(newName, StringComparison.OrdinalIgnoreCase))
        {
            AddAgent(newDefinition, isInitialLoad: false);
            return;
        }

        if (Agents.ContainsKey(newName))
        {
            SendLogLine($"System: Cannot rename — an agent named '{newName}' already exists.");
            return;
        }

        if (IsBuiltInAgent(newName))
        {
            SendLogLine($"System: The name '{newName}' is reserved for a built-in agent.");
            return;
        }

        foreach (var kv in Agents.ToList())
        {
            if (IsBuiltInAgent(kv.Key)) continue;
            if (kv.Key.Equals(currentKey, StringComparison.OrdinalIgnoreCase)) continue;
            var d = kv.Value.Definition;
            if (!d.NextAgentName.Equals(currentKey, StringComparison.OrdinalIgnoreCase)) continue;
            AddAgent(CloneDefinitionWithNext(d, newName), isInitialLoad: false);
        }

        Agents.Remove(currentKey);
        AddAgent(newDefinition, isInitialLoad: false, renamedFromKey: currentKey);
    }

    /// <summary>Removes a user-defined agent from the graph and persists <c>agents.json</c>. Built-in agents cannot be removed.</summary>
    public bool RemoveAgent(string name)
    {
        if (!Agents.ContainsKey(name) || IsBuiltInAgent(name) || IsHiddenSystemAgent(name))
            return false;

        Agents.Remove(name);

        foreach (var agent in Agents.Values.ToList())
        {
            if (IsBuiltInAgent(agent.Definition.Name))
                continue;
            if (!agent.Definition.NextAgentName.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var d = agent.Definition;
            AddAgent(new AgentDefinition
            {
                Name = d.Name,
                Role = d.Role,
                Description = d.Description,
                Instructions = d.Instructions,
                NextAgentName = OrchestratorAgentName,
                EnabledTools = d.EnabledTools.ToList(),
                AccentColor = d.AccentColor,
                IsChainEntry = d.IsChainEntry,
                JsonSchema = d.JsonSchema,
                LlmProvider = d.LlmProvider,
                ModelId = d.ModelId
            }, isInitialLoad: false);
        }

        if (Agents.TryGetValue(OrchestratorAgentName, out var orchestrator))
            UpdateOrchestratorPrompt(orchestrator);

        SaveAgentDefinitions();
        _session.State.RemoveHandoffReferencesToAgent(name);
        _session.State.RemoveNodeLayout(name);
        PruneWorkflowEdgesForRemovedAgent(name);
        RemoveModelStreamMonitor(name);
        OnHandoffTopologyChanged?.Invoke();
        SendLogLine($"System: Agent '{name}' removed.");
        return true;
    }

    /// <summary>Sets the wired handoff target for <paramref name="fromAgent"/> (graph / StateMap). Built-ins persist overrides; custom agents update <c>agents.json</c>.</summary>
    public void SetHandoffTarget(string fromAgent, string? toAgent)
    {
        if (_chatClient == null)
            return;
        if (string.IsNullOrWhiteSpace(fromAgent)
            || fromAgent.Equals(OrchestratorAgentName, StringComparison.OrdinalIgnoreCase))
            return;
        if (!Agents.TryGetValue(fromAgent, out var agent))
            return;

        var next = string.IsNullOrWhiteSpace(toAgent)
            || toAgent.Equals(OrchestratorAgentName, StringComparison.OrdinalIgnoreCase)
            ? OrchestratorAgentName
            : toAgent.Trim();

        if (!next.Equals(OrchestratorAgentName, StringComparison.OrdinalIgnoreCase)
            && !Agents.ContainsKey(next))
            return;

        Agents[fromAgent] = CreateAgentWithTools(CloneDefinitionWithNext(agent.Definition, next));

        if (IsBuiltInAgent(fromAgent))
            _session.State.SetHandoffOverride(fromAgent, next.Equals(OrchestratorAgentName, StringComparison.OrdinalIgnoreCase) ? null : next);
        else
            SaveAgentDefinitions();

        if (Agents.TryGetValue(OrchestratorAgentName, out var orchestrator))
            UpdateOrchestratorPrompt(orchestrator);

        SyncDefaultWorkflowEdgeWithHandoff(fromAgent, next);
    }

    private void SaveAgentDefinitions()
    {
        try
        {
            var dynamicAgents = Agents.Values
                .Where(a => !string.Equals(a.Definition.Name, OrchestratorAgentName, StringComparison.OrdinalIgnoreCase)
                            && !IsHiddenSystemAgent(a.Definition.Name))
                .Select(a => a.Definition)
                .ToList();
            _session.State.SaveCustomAgentDefinitions(dynamicAgents);
        }
        catch (Exception ex) { SendLogLine($"Warning: Failed to save agent definitions: {ex.Message}"); }
    }

    private void LoadDynamicAgents(bool persistToolFiltering, bool enableRecoveryTools)
    {
        try
        {
            var defs = _session.State.LoadCustomAgentDefinitions().ToList();
            var persistDefinitions = false;

            foreach (var def in defs)
            {
                if (IsHiddenSystemAgent(def.Name))
                    continue;

                var filtered = CloneDefinitionWithFilteredTools(def);
                var loaded = enableRecoveryTools ? EnsureRecoveryTools(filtered) : filtered;
                if (persistToolFiltering && EnabledToolSetsDiffer(def.EnabledTools, filtered.EnabledTools))
                    persistDefinitions = true;

                AddAgent(loaded, isInitialLoad: true);
            }

            var yamlDir = Path.Combine(_workspacePath, ".sharpwire", "agents");
            foreach (var def in DeclarativeAgentYaml.LoadFromDirectory(yamlDir))
            {
                if (Agents.ContainsKey(def.Name))
                {
                    SendLogLine($"Warning: Skipping YAML agent '{def.Name}' — name already exists.");
                    continue;
                }

                var yamlFiltered = CloneDefinitionWithFilteredTools(def);
                var loadedYaml = enableRecoveryTools ? EnsureRecoveryTools(yamlFiltered) : yamlFiltered;
                if (persistToolFiltering && EnabledToolSetsDiffer(def.EnabledTools, yamlFiltered.EnabledTools))
                    persistDefinitions = true;

                AddAgent(loadedYaml, isInitialLoad: true);
            }

            DeclarativeAgentYaml.MergeReturnWorkflowEdgesFromDirectory(yamlDir, _session.State, Agents.Keys);

            if (persistToolFiltering && persistDefinitions)
                SaveAgentDefinitions();
        }
        catch (Exception ex) { SendLogLine($"Warning: Failed to load dynamic agents: {ex.Message}"); }
    }

    private void ApplyBuiltInHandoffOverrides()
    {
        if (_chatClient == null)
            return;
        foreach (var kv in _session.State.LoadHandoffOverrides())
        {
            if (!IsBuiltInAgent(kv.Key) || !Agents.TryGetValue(kv.Key, out var ag))
                continue;
            if (!Agents.ContainsKey(kv.Value))
                continue;
            Agents[kv.Key] = CreateAgentWithTools(CloneDefinitionWithNext(ag.Definition, kv.Value));
        }
    }

    private string FormatPluginSettingsRegistryLine()
    {
        return PluginSettingsDefinitions.Count == 0
            ? "No compiled plugin currently declares user-visible settings (see PLUGIN CONSTRUCTION below to add them).\n"
            : "These compiled plugins expose user-editable settings in the app (Settings → LOADED PLUGINS (SETTINGS) → Edit). Values persist in .sharpwire/settings.json under pluginSettings: "
              + string.Join(", ", PluginSettingsDefinitions.Select(p => $"'{p.PluginName}'")) + ".\n";
    }

    private string BuildSharpwirePluginAuthoringReference(string? pluginSettingsRegistryLine = null)
    {
        var pluginSettingsRegistry = pluginSettingsRegistryLine ?? FormatPluginSettingsRegistryLine();
        return "PLUGIN SETTINGS (host UI):\n" +
               "- The Sharpwire desktop app can surface per-plugin settings (API keys, feature toggles, endpoints, etc.) that you do NOT set via chat: the user edits them under Settings → LOADED PLUGINS (SETTINGS) and saves from each plugin's tab.\n" +
               "- When helping the user configure a plugin, direct them to that UI (or .sharpwire/settings.json → pluginSettings) rather than inventing values in conversation.\n" +
               pluginSettingsRegistry + "\n" +
               "PLUGIN CONSTRUCTION (C#):\n" +
               "- New tools are defined by placing .cs files under the workspace `plugins/` folder and/or the host global plugins folder (per-user, all workspaces); all such files compile together into one assembly.\n" +
               "- Plugin-local dependencies are supported by placing DLLs in a sibling `lib/` folder next to your plugin source file(s). Example layout: `plugins/MyPlugin/MyPlugin.cs` with dependencies in `plugins/MyPlugin/lib/*.dll`.\n" +
               "- Use natural language names for methods and classes.\n" +
               "- EVERY public method you want to expose as a tool MUST have a [System.ComponentModel.Description(\"...\")] attribute.\n" +
               "- Input parameters should also have [Description] attributes for better tool-calling quality.\n" +
               "- Reference BCL / standard .NET types (string, int, bool, Task, etc.). Other Sharpwire.* namespaces are NOT available in plugin code EXCEPT Sharpwire.Core.MetaToolbox (plugin settings) and Sharpwire.Core.Hooks (lifecycle middleware):\n" +
               "  • [Sharpwire.Core.MetaToolbox.PluginSettings(\"Plugin display name\")] on a class, plus [Sharpwire.Core.MetaToolbox.PluginSetting(\"Label\", \"Help text\", isSecret: true|false)] on public properties to register fields in the LOADED PLUGINS UI.\n" +
               "  • Optional: implement Sharpwire.Core.MetaToolbox.IPluginWithSettings on the SAME class as your tool methods so OnSettingsLoaded(Dictionary<string,object>) runs after compile and when the user saves that tab (same instance the LLM invokes).\n" +
               "- Optional: ILifecycleHookMiddleware for lifecycle events (orchestrator turn, agent run/step, handoffs, tool approval). Mutate Task/ResponseText/approval text carefully; call Block(\"reason\") and return without calling next to deny; on handoffs only retarget ToAgent to existing agents. The same class can expose tools via [Description] methods.\n" +
               "- Plugins can emit system chat notes via Sharpwire.Core.Hooks.PluginChat.TryPostSystem(\"message\", \"PluginName\"). This is rate-limited and for concise diagnostics only.\n" +
               "- Hook context types you can branch on: OrchestratorTurnHookContext, AgentExecutionHookContext, AgentStepHookContext, ChainHandoffHookContext, ToolApprovalHookContext.\n" +
               "- Tool approval hooks use Sharpwire.Core.Hooks.ToolApprovalHookContext with ToolId, Details, and Approved. There is NO Arguments property on this type in Sharpwire.\n" +
               "- If you parse structured data from Details, use TryParse/TryGet patterns and initialize locals to avoid unassigned-local compile errors.\n" +
               "- Minimal hook skeleton (safe pattern):\n" +
               "  using Sharpwire.Core.Hooks;\n" +
               "  using System.Threading;\n" +
               "  using System.Threading.Tasks;\n" +
               "  public sealed class MyHook : ILifecycleHookMiddleware {\n" +
               "    public int Order => 100;\n" +
               "    public async Task InvokeAsync(LifecycleHookContext context, LifecycleHookNext next, CancellationToken ct) {\n" +
               "      switch (context) {\n" +
               "        case OrchestratorTurnHookContext o when o.Stage == LifecycleHookStage.OrchestratorTurnStart:\n" +
               "          o.Task = o.Task.Trim();\n" +
               "          break;\n" +
               "        case AgentStepHookContext s when s.Stage == LifecycleHookStage.AgentStepEnd:\n" +
               "          s.ResponseText = s.ResponseText?.Trim();\n" +
               "          break;\n" +
               "        case ChainHandoffHookContext h:\n" +
               "          // optional: retarget h.ToAgent to an existing agent\n" +
               "          break;\n" +
               "        case ToolApprovalHookContext t when t.Stage == LifecycleHookStage.ToolApprovalRequest:\n" +
               "          var details = t.Details ?? string.Empty; // NO t.Arguments in Sharpwire\n" +
               "          if (details.Contains(\"..\")) { t.Block(\"Blocked suspicious path pattern.\"); return; }\n" +
               "          break;\n" +
               "      }\n" +
               "      await next(ct).ConfigureAwait(false);\n" +
               "    }\n" +
               "  }\n" +
               "- Example tool-only plugin:\n" +
               "  using System.ComponentModel;\n" +
               "  public class MyNewTools { [Description(\"Does X\")] public string DoX(string input) => ...; }\n" +
               "- Example settings + tools (same class): add PluginSettings / PluginSetting attributes on properties and optionally IPluginWithSettings; read values in your tool methods after OnSettingsLoaded.";
    }

    private static string BuildLifecycleHooksReference() =>
        "LIFECYCLE HOOKS REFERENCE:\n" +
        "- Stages: OrchestratorTurnStart, OrchestratorTurnEnd, AgentExecutionStart, AgentExecutionEnd, AgentStepStart, AgentStepEnd, ChainHandoff, ToolApprovalRequest, ToolApprovalResult.\n" +
        "- Context types: OrchestratorTurnHookContext, AgentExecutionHookContext, AgentStepHookContext, ChainHandoffHookContext, ToolApprovalHookContext.\n" +
        "- ToolApprovalHookContext fields: ToolId, Details, Approved. There is no Arguments property.\n" +
        "- Common controls: context.Block(\"reason\"), CorrelationId, Stage.";

    public string GetSharpwireDocumentationAnswer(string question)
    {
        var q = (question ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(q))
            return "Please ask a specific question about Sharpwire (hooks, plugin construction, settings, orchestration, or tool approval).";

        var hooksRef = BuildLifecycleHooksReference();
        var pluginRef = BuildSharpwirePluginAuthoringReference();
        var lower = q.ToLowerInvariant();

        if (lower.Contains("hook") || lower.Contains("lifecycle") || lower.Contains("toolapprovalhookcontext") || lower.Contains("tool approval"))
            return hooksRef + "\n\n" + pluginRef;

        if (lower.Contains("plugin") || lower.Contains("settings") || lower.Contains("onsettingsloaded") || lower.Contains("tool"))
            return pluginRef + "\n\n" + hooksRef;

        return hooksRef + "\n\n" + pluginRef;
    }

    private string BuildSharpwireDesignReviewInstructions()
    {
        var workspacePlugins = Path.Combine(_workspacePath, "plugins");
        var globalPlugins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sharpwire", "plugins");
        var toolsHint = string.Join(", ", AvailableTools.Keys.Where(k =>
            !k.Contains("Agent", StringComparison.OrdinalIgnoreCase)
            && !k.Contains("TriggerChain_", StringComparison.OrdinalIgnoreCase)));

        return "You are Sharpwire's internal design reviewer. You do NOT create files, run workspace tools, or apply changes yourself. The host passes you proposed agent definitions, workflow edges, plugin C#, and/or settings-related plans. Sanity-check them against Sharpwire before another component applies them.\n\n" +
               "Currently registered tool IDs relevant to non-orchestrator agents (orchestrator-only tools may also exist): " + toolsHint + "\n\n" +
               "AGENT & WORKFLOW RULES:\n" +
               "- Prefer natural language agent names with spaces for display (e.g. 'Release Engineer'). Avoid PascalCase-only or snake_case as the primary name.\n" +
               "- For critic/reviewer loops, ensure Return/review workflow edges are specified so work can route back to the author when review fails.\n" +
               "- Enabled tool IDs on an agent must match real host tool names when those tools are required.\n" +
               "- ApproveWork is host-injected only when an agent has an outgoing Return edge; it is not in EnabledTools or the tool list above—do not treat it as missing in that case. Flag instructions that mention ApproveWork when there is no Return edge.\n\n" +
               "EVIDENCE RULES (CRITICAL):\n" +
               "- For each blocking issue, cite at least one concrete host API/type/member name (for example: ToolApprovalHookContext.Details, LifecycleHookStage.ToolApprovalRequest, IPluginWithSettings.OnSettingsLoaded).\n" +
               "- If you cannot cite a concrete API/member from Sharpwire's known surface, downgrade to warning and label it \"uncertain\".\n" +
               "- Never invent members (for example, do not claim ToolApprovalHookContext.Arguments exists).\n\n" +
               "PLUGIN PATHS (all .cs in both compile into one assembly):\n" +
               $"- Workspace: {workspacePlugins}\n" +
               $"- Global (per user, all workspaces): {globalPlugins}\n" +
               "- Recompilation: TOOLS → RELOAD PLUGINS or restart.\n\n" +
               BuildSharpwirePluginAuthoringReference() + "\n\n" +
               "OUTPUT: (1) Blocking issues — must fix before apply, (2) Warnings, (3) Verdict — OK to proceed / not OK. Be concise.";
    }

    private void UpdateSharpwireDesignReviewAgentPrompt(Agent agent)
    {
        agent.DynamicInstructions = BuildSharpwireDesignReviewInstructions();
    }

    private void UpdateOrchestratorPrompt(Agent orchestrator)
    {
        // Rebuild orchestrator tools to include dynamic chain tools
        orchestrator.Tools.Clear();
        foreach (var toolId in orchestrator.Definition.EnabledTools)
        {
            if (AvailableTools.TryGetValue(toolId, out var tool))
                orchestrator.Tools.Add(tool);
        }

        var chains = Agents.Values.Where(a => a.Definition.IsChainEntry && !IsBuiltInAgent(a.Definition.Name)).ToList();

        foreach (var chain in chains)
        {
            var captureName = chain.Definition.Name;
            var toolName = "TriggerChain_" + captureName.Replace(" ", "_");
            var toolDesc = $"Triggers the pre-defined node chain starting at '{captureName}'. Use this macro-function for tasks matching: {chain.Definition.Description}. You must provide 'instructions'.";
            
            Func<string, Task<string>> chainDelegate = async (string instructions) =>
            {
                this.Log($"Orchestrator (MagneticManager): Routing to chain '{captureName}' with instructions: {instructions}");
                this.RaiseAgentActivity(OrchestratorAgentName, AgentActivityKind.ToolUse);
                try
                {
                    return await Task.Run(async () =>
                            await this.ExecuteAgentAsync(captureName, instructions, CancellationToken.None)
                                .ConfigureAwait(false),
                        CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    this.RaiseAgentActivity(OrchestratorAgentName, AgentActivityKind.Thinking);
                }
            };
            
            try
            {
                var tool = AIFunctionFactory.Create(chainDelegate, toolName, toolDesc);
                orchestrator.Tools.Add(tool);
            }
            catch (Exception ex)
            {
                this.Log($"Warning: Failed to create tool for chain '{captureName}': {ex.Message}");
            }
        }

        // Only show agents explicitly marked as entry points.
        var visibleAgents = Agents.Values
            .Where(a => a.Definition.Name != OrchestratorAgentName)
            .Where(a => a.Definition.IsChainEntry)
            .ToList();

        var agentList = string.Join("\n", visibleAgents
            .Select(a => $"- '{a.Definition.Name}': {a.Definition.Description} (Handoff to: {a.Definition.NextAgentName})"));

        var assignableToolIds = string.Join(", ", AvailableTools.Keys.Where(k => !k.Contains("Agent", StringComparison.OrdinalIgnoreCase) && !k.Contains("TriggerChain_", StringComparison.OrdinalIgnoreCase)));
        var orchestratorCallableTools = string.Join(", ", orchestrator.Tools.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase));

        var chainEntryList = string.Join(", ", chains.Select(a => $"'{a.Definition.Name}'"));
        var chainEntryBlock = string.IsNullOrEmpty(chainEntryList)
            ? string.Empty
            : "WIRED CHAIN ENTRY: We have dynamic tools for the following pre-defined chains: "
              + chainEntryList + ".\nPrefer using the specific 'TriggerChain_...' tool over 'DelegateToAgent' when a task matches a chain's description.\n\n";

        var newInstructions = "You are the System Orchestrator. You coordinate other agents; you do not personally write code, run their tools, or create files in the workspace.\n\n" +
                               "AVAILABLE AGENTS:\n" + agentList + "\n\n" +
                               "ORCHESTRATOR-CALLABLE TOOLS (only these can be called by you directly):\n" + orchestratorCallableTools + "\n\n" +
                               "ASSIGNABLE AGENT TOOL IDS (for CreateNewAgent/CreateAgentChain only; not directly callable by you):\n" + assignableToolIds + "\n\n" +
                               "NAMING CONVENTION (CRITICAL):\n" +
                               "- ALWAYS use natural language with spaces for agent names (e.g., 'File Manager', 'Python Expert', 'Security Auditor').\n" +
                               "- NEVER use PascalCase (FileManager), camelCase (fileManager), or snake_case (file_manager).\n\n" +
                               chainEntryBlock +
                               "Delegation:\n" +
                               "- ALWAYS check if an existing agent has the specialty or tools needed for a task before creating a new one.\n" +
                               "- You MUST use DelegateToAgent to assign tasks. The tool returns that agent's reply—that output is *their* work, not yours.\n" +
                               "- Never call ReadFile/ListFiles/WriteFile directly from the orchestrator. Those are agent tools, not orchestrator tools.\n\n" +
                               "Workflow Design:\n" +
                               "- **Wired Chains (`CreateAgentChain`)**: Use this for complex, multi-step processes that should run autonomously (e.g., Writer -> Literary Agent -> Publisher). Set `NextAgentName` to the next agent in the sequence and set `IsChainEntry: true` only on the first agent. This is the preferred way to build robust, repeatable pipelines.\n" +
                               "- **Feedback Loops (CRITICAL)**: If a chain includes a critic, editor, or reviewer (e.g., Writer -> Editor), you MUST add a 'Return' connection from the Editor back to the Writer in the `connections` list. This enables autonomous iteration until the Editor is satisfied.\n" +
                               "- **Standalone Experts (`CreateNewAgent`)**: Use this for single-purpose agents that you intend to manage directly. These agents hand off back to you ('Orchestrator') by default.\n\n" +
                               "Credit and voice (critical):\n" +
                               "- Never take credit for another agent's work. Do not say \"I created\", \"I wrote\", \"I have built\", \"I implemented\", or \"my files\" for anything they produced.\n" +
                               "- The user already sees each agent's message in chat under that name. Do not repeat their detailed report in your own voice as if you did the same work.\n" +
                               "- When you attribute work, use the agent's bare name only (e.g. Coder, Poet, Reviewer)—not \"the Coder agent\", \"the Poet agent\", or similar.\n" +
                               "- After delegation, reply briefly as coordinator only: who did what, whether the user's goal seems satisfied, and any next steps. Use names like \"Coder added …\" or \"Poet wrote …\" if you mention specifics.\n" +
                               "- If another agent's answer already fully answers the user, a short closing line is enough—do not re-narrate their implementation as yours.\n\n" +
                               "Design validation:\n" +
                               "- `CreateNewAgent` and `CreateAgentChain` run an internal design review before applying changes; incorporate the feedback returned in the tool result.\n" +
                               "- Call `ReviewProposedSharpwireDesign` with draft text before delegating plugin authoring to Coder (or any time you want a Sharpwire-specific sanity check). If plugin compilation fails, the host posts one system message with an automatic review of the error.\n\n" +
                               BuildSharpwirePluginAuthoringReference() + "\n\n" +
                               "You may use CreateNewAgent or CreateAgentChain if the user's request requires a specialty or workflow that does not exist yet.";
        
        orchestrator.DynamicInstructions = newInstructions;
        OnOrchestratorPromptUpdated?.Invoke(newInstructions);

        if (Agents.TryGetValue(SharpwireDesignReviewAgentName, out var designReview))
            UpdateSharpwireDesignReviewAgentPrompt(designReview);
    }

    /// <summary>Runs the hidden design-review agent on a proposal (no chat UI, no tools). Refreshes its prompt from current tools/plugins first.</summary>
    public async Task<string> ReviewProposedSharpwireDesignAsync(string proposalPayload, CancellationToken ct)
    {
        if (!Agents.TryGetValue(SharpwireDesignReviewAgentName, out var agent))
            return $"Error: '{SharpwireDesignReviewAgentName}' is not initialized.";

        UpdateSharpwireDesignReviewAgentPrompt(agent);

        var userPrompt =
            "Review the following proposed Sharpwire design. It has NOT been applied yet. Do not assume files or agents already exist unless stated in the proposal.\n\n" +
            proposalPayload;

        var response = await agent.GetResponseAsync(
            userPrompt,
            new List<ChatMessage>(),
            ct,
            onStreamText: null,
            appendReviewerInstruction: false).ConfigureAwait(false);

        return LastNonEmptyAssistantText(response);
    }

    /// <summary>Snapshot of current agents, workflow edges, tools, and plugin settings for validation from Settings (etc.).</summary>
    public string BuildAmbientDesignReviewPayload()
    {
        static string Clip(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "(none)";
            var t = text.Trim();
            return t.Length <= max ? t : t[..max] + "…";
        }

        var workspacePlugins = Path.Combine(_workspacePath, "plugins");
        var globalPlugins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sharpwire", "plugins");
        var tools = string.Join(", ", AvailableTools.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        var settingsSummary = PluginSettingsDefinitions.Count == 0
            ? "(none)"
            : string.Join("; ", PluginSettingsDefinitions.Select(p =>
                $"{p.PluginName}: {p.Properties.Count} setting(s)"));

        var agentBlocks = Agents.Values
            .Where(a => !IsHiddenSystemAgent(a.Definition.Name))
            .OrderBy(a => a.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a =>
            {
                var d = a.Definition;
                var toolList = d.EnabledTools == null || d.EnabledTools.Count == 0
                    ? "(none)"
                    : string.Join(", ", d.EnabledTools.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
                var model = string.IsNullOrWhiteSpace(d.LlmProvider) && string.IsNullOrWhiteSpace(d.ModelId)
                    ? "(workspace default)"
                    : $"{d.LlmProvider ?? "default"} / {d.ModelId ?? "default"}";
                return $"— {d.Name}\n" +
                       $"  role: {Clip(d.Role, 120)}\n" +
                       $"  description: {Clip(d.Description, 200)}\n" +
                       $"  next: {d.NextAgentName}; chain entry: {d.IsChainEntry}; model: {model}\n" +
                       $"  tools: {Clip(toolList, 600)}\n" +
                       $"  instructions (excerpt): {Clip(d.Instructions, 900)}";
            });
        var agentsSection = Agents.Values.Any(a => !IsHiddenSystemAgent(a.Definition.Name))
            ? string.Join("\n\n", agentBlocks)
            : "(no agents loaded)";

        var edges = _session.State.LoadWorkflowEdges();
        string edgesSection;
        if (edges.Count == 0)
            edgesSection = "(none)";
        else
        {
            const int maxEdges = 80;
            var lines = edges.Take(maxEdges).Select(e =>
                $"{e.From} → {e.To} ({e.Kind}, condition: {e.ConditionRef ?? "always"})");
            edgesSection = string.Join("\n", lines);
            if (edges.Count > maxEdges)
                edgesSection += $"\n… and {edges.Count - maxEdges} more edge(s).";
        }

        return "Sharpwire validation run: review the CURRENT workspace agents, workflow edges, registered tools, and plugin metadata below.\n\n" +
               $"Workspace plugins directory: {workspacePlugins}\n" +
               $"Global plugins directory: {globalPlugins}\n\n" +
               "### Agents (loaded in this session)\n" +
               agentsSection + "\n\n" +
               "### Workflow edges (persisted)\n" +
               edgesSection + "\n\n" +
               "### Registered tool names\n" +
               tools + "\n\n" +
               "### Plugins exposing settings UI\n" +
               settingsSummary + "\n\n" +
               "Validate against Sharpwire agent/plugin/workflow conventions; flag structural issues, risky tool sets, missing feedback-loop wiring, and plugin-hosting problems.";
    }

    private async Task PostPluginCompileFailureDesignReviewAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(LastPluginError))
            return;

        OnAgentStateChanged?.Invoke(OrchestratorAgentName, AgentState.Busy);
        OnAgentActivityChanged?.Invoke(OrchestratorAgentName, AgentActivityKind.Thinking);
        try
        {
            var payload =
                "Context: Workspace plugin compilation FAILED in this session. Help interpret the error under Sharpwire rules (e.g. [Description] on tools, Sharpwire.Core.MetaToolbox-only usage from Sharpwire.*, valid C# for Roslyn in this host).\n\n" +
                "Compiler / host message:\n" + LastPluginError + "\n\n" +
                BuildAmbientDesignReviewPayload();
            var result = await ReviewProposedSharpwireDesignAsync(payload, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result))
                return;

            var orchestratorPrompt =
                "I reviewed the plugin compilation failure and have a validator report:\n\n"
                + result
                + "\n\nShould I start resolving these plugin compile errors now?";
            _session.Transcript.AppendMessages(new[]
            {
                new ChatMessage(ChatRole.Assistant, orchestratorPrompt)
            });
            EmitChatMessage(
                MessageRole.Agent,
                orchestratorPrompt,
                OrchestratorAgentName,
                AccentForSender(OrchestratorAgentName));
        }
        catch (Exception ex)
        {
            SendLogLine($"Design review after plugin compile error skipped: {ex.Message}");
        }
        finally
        {
            OnAgentActivityChanged?.Invoke(OrchestratorAgentName, AgentActivityKind.Idle);
            OnAgentStateChanged?.Invoke(OrchestratorAgentName, AgentState.Success);
        }
    }

    private void ReportProviderStatus(string name, bool isConnected, string status)
    {
        OnProviderStatusChanged?.Invoke(name, isConnected, status);
    }

    private IChatClient ResolveChatClient(AgentDefinition definition)
    {
        var settings = _settingsManager.Load(_apiKeyStore);
        var provider = (string.IsNullOrWhiteSpace(definition.LlmProvider) 
            ? (string.IsNullOrWhiteSpace(settings.LlmProvider) ? "Gemini" : settings.LlmProvider)
            : definition.LlmProvider).Trim();

        var modelId = (string.IsNullOrWhiteSpace(definition.ModelId)
            ? (!string.IsNullOrWhiteSpace(settings.ModelId) ? settings.ModelId : settings.GeminiModelId)
            : definition.ModelId)?.Trim();

        if (string.IsNullOrEmpty(modelId))
        {
            modelId = provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase) ? "gemini-2.5-flash" : "gpt-4o";
        }

        IChatClient? client = null;

        if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = _apiKeyStore.GetGoogleApiKey();
            if (!string.IsNullOrEmpty(apiKey))
                client = MafGoogleGeminiChatClient.Create(apiKey, modelId);
        }
        else if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = _apiKeyStore.GetOpenAiApiKey();
            if (!string.IsNullOrEmpty(apiKey))
            {
                client = new OpenAI.Chat.ChatClient(modelId, new System.ClientModel.ApiKeyCredential(apiKey))
                    .AsIChatClient()
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();
            }
        }
        else if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = _apiKeyStore.GetAnthropicApiKey();
            if (!string.IsNullOrEmpty(apiKey))
            {
                client = new Anthropic.AnthropicClient { ApiKey = apiKey }
                    .AsIChatClient(modelId)
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();
            }
        }
        else if (provider.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = string.IsNullOrWhiteSpace(settings.CustomEndpoint) ? AppSettings.DefaultCustomEndpoint : settings.CustomEndpoint;
            var options = new OpenAI.OpenAIClientOptions();
            if (!string.IsNullOrEmpty(endpoint)) options.Endpoint = new Uri(endpoint);
            // Custom/Local might use OpenAI key or none
            var apiKey = _apiKeyStore.GetOpenAiApiKey() ?? "none";
            client = new OpenAI.Chat.ChatClient(modelId, new System.ClientModel.ApiKeyCredential(apiKey), options)
                .AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .Build();
        }

        return client ?? _chatClient ?? new MockChatClient();
    }

    public async Task InitializeAgentsAsync(CancellationToken ct)
    {
        try 
        {
            SendLogLine("System: Initializing Agent Service...");
            
            var settings = _settingsManager.Load(_apiKeyStore);
            var googleKey = _apiKeyStore.GetGoogleApiKey();
            var openAiKey = _apiKeyStore.GetOpenAiApiKey();
            var anthropicKey = _apiKeyStore.GetAnthropicApiKey();

            // Fire and forget dynamic model fetching; it will update the UI via events.
            _ = FetchModelsAsync(ct);

            var globalProvider = (string.IsNullOrWhiteSpace(settings.LlmProvider) ? "Gemini" : settings.LlmProvider).Trim();
            
            // Model resolution: env > settings.ModelId > settings.GeminiModelId (legacy)
            var modelId = Environment.GetEnvironmentVariable("GOOGLE_GENAI_MODEL")?.Trim();
            if (string.IsNullOrEmpty(modelId))
            {
                modelId = !string.IsNullOrWhiteSpace(settings.ModelId) 
                    ? settings.ModelId.Trim() 
                    : settings.GeminiModelId.Trim();
            }

            // Status Reporting for all providers
            ReportProviderStatus("Gemini", !string.IsNullOrEmpty(googleKey), string.IsNullOrEmpty(googleKey) ? "OFFLINE" : "READY");
            ReportProviderStatus("OpenAI", !string.IsNullOrEmpty(openAiKey), string.IsNullOrEmpty(openAiKey) ? "OFFLINE" : "READY");
            ReportProviderStatus("Anthropic", !string.IsNullOrEmpty(anthropicKey), string.IsNullOrEmpty(anthropicKey) ? "OFFLINE" : "READY");
            ReportProviderStatus("Custom", true, string.IsNullOrWhiteSpace(settings.CustomEndpoint) ? "DEFAULT" : "READY");

            if (globalProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(googleKey))
            {
                SendLogLine($"System: Initializing Gemini, model '{modelId}'...");
                _chatClient = MafGoogleGeminiChatClient.Create(googleKey, modelId);
            }
            else if (globalProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(openAiKey))
            {
                SendLogLine($"System: Initializing OpenAI, model '{modelId}'...");
                _chatClient = new OpenAI.Chat.ChatClient(modelId, new System.ClientModel.ApiKeyCredential(openAiKey))
                    .AsIChatClient()
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();
            }
            else if (globalProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(anthropicKey))
            {
                SendLogLine($"System: Initializing Anthropic, model '{modelId}'...");
                _chatClient = new Anthropic.AnthropicClient { ApiKey = anthropicKey }
                    .AsIChatClient(modelId)
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();
            }
            else if (globalProvider.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                var endpoint = string.IsNullOrWhiteSpace(settings.CustomEndpoint) ? AppSettings.DefaultCustomEndpoint : settings.CustomEndpoint;
                SendLogLine($"System: Initializing Custom/Local endpoint '{endpoint}', model '{modelId}'...");
                
                var options = new OpenAI.OpenAIClientOptions();
                if (!string.IsNullOrEmpty(endpoint))
                {
                    options.Endpoint = new Uri(endpoint);
                }
                
                var localKey = string.IsNullOrEmpty(openAiKey) ? "none" : openAiKey;
                _chatClient = new OpenAI.Chat.ChatClient(modelId, new System.ClientModel.ApiKeyCredential(localKey), options)
                    .AsIChatClient()
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();
            }
            else
            {
                SendLogLine($"System: Global Provider '{globalProvider}' not configured or key missing. Initializing Mock client...");
                _chatClient = new MockChatClient(); 
            }

            SendLogLine("System: Registering tools...");
            AvailableTools.Clear();
            PluginSettingsDefinitions = new List<PluginSettingInfo>();
            _dynamicPluginsAssembly = null;
            _dynamicPluginInstances.Clear();
            _lifecycleHookPipeline = LifecycleHookPipeline.Empty;
            _toolApproval.SetLifecycleHookPipeline(_lifecycleHookPipeline);

            // LocalShellPlugin is not registered: STANDARDS forbid passing raw LLM output to a shell.

            var fileIO = new FileIOPlugin(_workspacePath, _toolApproval);
            foreach (var method in typeof(FileIOPlugin).GetMethods().Where(m => m.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Any()))
            {
                 AvailableTools[method.Name] = AIFunctionFactory.Create(method, fileIO);
            }

            var orchestratorPlugin = new OrchestratorPlugin(this, _settingsManager);
            foreach (var method in typeof(OrchestratorPlugin).GetMethods().Where(m => m.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Any()))
            {
                 AvailableTools[method.Name] = AIFunctionFactory.Create(method, orchestratorPlugin);
            }

            var docsPlugin = new SharpwireDocumentationPlugin(this);
            foreach (var method in typeof(SharpwireDocumentationPlugin).GetMethods().Where(m => m.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Any()))
            {
                AvailableTools[method.Name] = AIFunctionFactory.Create(method, docsPlugin);
            }

            var pluginCompileFailedThisRun = false;
            try
            {
                LastPluginError = null;
                OnPluginStatusUpdated?.Invoke(true, null);

                var assembly = _pluginCompiler.CompilePlugins();
                if (assembly != null)
                {
                    var (dynamicTools, pluginSettingInfos, instancesByType, lifecycleHooks) = _pluginCompiler.ExtractPlugins(assembly);
                    foreach (var kv in dynamicTools)
                    {
                        AvailableTools[kv.Key] = kv.Value;
                    }
                    PluginSettingsDefinitions = pluginSettingInfos;
                    _dynamicPluginsAssembly = assembly;
                    _dynamicPluginInstances = instancesByType;
                    _lifecycleHookPipeline = new LifecycleHookPipeline(lifecycleHooks);
                    _toolApproval.SetLifecycleHookPipeline(_lifecycleHookPipeline);
                    SendLogLine("System: Dynamic plugins compiled and registered.");
                    if (lifecycleHooks.Count == 0)
                        SendLogLine("System: Lifecycle hooks: (none)");
                    else
                    {
                        var hookNames = lifecycleHooks
                            .Select(h => h.GetType().FullName ?? h.GetType().Name)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(n => n, StringComparer.Ordinal);
                        SendLogLine("System: Lifecycle hooks: " + string.Join(", ", hookNames));
                    }
                    OnPluginStatusUpdated?.Invoke(false, null);
                    foreach (var pluginDef in pluginSettingInfos)
                        DispatchPluginSettingsLoaded(pluginDef.PluginName);
                }
                else
                {
                    OnPluginStatusUpdated?.Invoke(false, null);
                }
            }
            catch (Exception ex)
            {
                pluginCompileFailedThisRun = true;
                LastPluginError = ex.Message;
                _dynamicPluginsAssembly = null;
                _dynamicPluginInstances.Clear();
                _lifecycleHookPipeline = LifecycleHookPipeline.Empty;
                _toolApproval.SetLifecycleHookPipeline(_lifecycleHookPipeline);
                SendLogLine($"Warning: Plugin compile error: {ex.Message}");
                OnPluginStatusUpdated?.Invoke(false, ex.Message);
            }

            SendLogLine("System: Creating agents...");
            Agents.Clear();

            // 1. Load existing dynamic agents (from agents.json and YAML directory)
            var agentsPath = Path.Combine(_workspacePath, ".sharpwire", "agents.json");
            var agentsFileExists = File.Exists(agentsPath);
            LoadDynamicAgents(
                persistToolFiltering: !pluginCompileFailedThisRun,
                enableRecoveryTools: pluginCompileFailedThisRun);

            // 2. Define core starter agents
            var coderDef = new AgentDefinition
            {
                Name = "Coder",
                Role = "Developer",
                Description = "Expert at writing Python code and saving files.",
                Instructions = "You are a Coder agent. Your goal is to solve the task by writing code and creating files in the workspace. Always use tools to implement and verify your work.",
                EnabledTools = AvailableTools.Keys.Where(k => !k.Contains("Agent", StringComparison.OrdinalIgnoreCase)).ToList(),
                AccentColor = "#0D7377",
                IsChainEntry = true
            };

            var reviewerDef = new AgentDefinition
            {
                Name = "Reviewer",
                Role = "QA",
                Description = "Expert at verifying code and checking for issues.",
                Instructions = "You are a Reviewer. Verify the prior agent's work in plain language. If something is wrong, say what should change; if it meets the task, say clearly that it passes your review. Never send an empty or whitespace-only reply—always include at least one short sentence (even if it is only confirmation).",
                EnabledTools = new List<string> { "ReadFile", "ListFiles" },
                AccentColor = "#7B4397"
            };

            // 3. Populate missing starters if they aren't in Agents yet AND it's a fresh workspace
            var addedStarters = false;
            if (!agentsFileExists)
            {
                if (settings.EnableCoder && !Agents.ContainsKey("Coder"))
                {
                    AddAgent(coderDef, isInitialLoad: true);
                    addedStarters = true;
                }
                if (settings.EnableReviewer && !Agents.ContainsKey("Reviewer"))
                {
                    AddAgent(reviewerDef, isInitialLoad: true);
                    addedStarters = true;
                }

                if (addedStarters)
                {
                    SaveAgentDefinitions();
                }
            }

            var orchestratorDef = new AgentDefinition
            {
                Name = OrchestratorAgentName,
                Role = "System Orchestrator",
                Description = "Routes tasks to specialized agents.",
                Instructions = "You are the System Orchestrator. Your job is to listen to the user's request, and delegate tasks to the specialized agents available to you.",
                EnabledTools = new List<string> { DelegateToAgentToolId, CreateNewAgentToolId, CreateAgentChainToolId, ReviewProposedSharpwireDesignToolId, AskSharpwireDocumentationToolId },
                AccentColor = "#3D6B9A"
            };

            AddAgent(orchestratorDef, isInitialLoad: true);

            var designReviewDef = new AgentDefinition
            {
                Name = SharpwireDesignReviewAgentName,
                Role = "Internal design review",
                Description = "Host-only; validates proposed agents and plugins before apply.",
                Instructions = "Internal Sharpwire design reviewer (full instructions in DynamicInstructions).",
                EnabledTools = new List<string>(),
                AccentColor = "#444444",
                IsChainEntry = false,
                NextAgentName = OrchestratorAgentName
            };
            AddAgent(designReviewDef, isInitialLoad: true);

            ApplyBuiltInHandoffOverrides();

            // 4. Finalize Orchestrator prompt
            UpdateOrchestratorPrompt(Agents[OrchestratorAgentName]);

            if (pluginCompileFailedThisRun)
                await PostPluginCompileFailureDesignReviewAsync(ct).ConfigureAwait(false);
            
            SendLogLine("System: Agents ready.");
            EnsureWorkflowEdgesMaterialized();
            PruneInvalidPersistedWorkflowEdges();
            OnAgentsReady?.Invoke();
        }
        catch (Exception ex)
        {
            SendLogLine($"CRITICAL ERROR during agent initialization: {ex.Message}");
        }
    }

    private async Task FetchModelsAsync(CancellationToken ct)
    {
        using var http = new HttpClient();
        try
        {
            var googleKey = _apiKeyStore.GetGoogleApiKey();
            if (!string.IsNullOrEmpty(googleKey))
            {
                try 
                {
                    var response = await http.GetStringAsync($"https://generativelanguage.googleapis.com/v1beta/models?key={googleKey}", ct);
                    using var doc = JsonDocument.Parse(response);
                    var list = new List<string>();
                    if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                    {
                        foreach (var m in modelsArray.EnumerateArray())
                        {
                            if (m.TryGetProperty("name", out var nameProp))
                            {
                                var fullId = nameProp.GetString() ?? "";
                                // SDK wants the ID without 'models/' prefix
                                var shortId = fullId.Replace("models/", "");
                                
                                // Filter for models that support generating content
                                bool supportsGeneration = false;
                                if (m.TryGetProperty("supportedGenerationMethods", out var methods))
                                {
                                    foreach (var method in methods.EnumerateArray())
                                    {
                                        if (method.GetString() == "generateContent")
                                        {
                                            supportsGeneration = true;
                                            break;
                                        }
                                    }
                                }

                                if (supportsGeneration)
                                    list.Add(shortId);
                            }
                        }
                    }
                    if (list.Count > 0)
                    {
                        GeminiModels = list.OrderBy(x => x).ToList();
                        OnModelsUpdated?.Invoke("Gemini", GeminiModels);
                    }
                }
                catch (Exception ex)
                {
                    SendLogLine($"Warning: Could not fetch Gemini models dynamically: {ex.Message}");
                }
            }

            var openAiKey = _apiKeyStore.GetOpenAiApiKey();
            if (!string.IsNullOrEmpty(openAiKey))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", openAiKey);
                    var response = await http.SendAsync(request, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(json);
                        var list = new List<string>();
                        if (doc.RootElement.TryGetProperty("data", out var dataArray))
                        {
                            foreach (var m in dataArray.EnumerateArray())
                            {
                                if (m.TryGetProperty("id", out var idProp))
                                {
                                    var id = idProp.GetString() ?? "";
                                    // Filter for chat models
                                    if (id.StartsWith("gpt") || id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4"))
                                        list.Add(id);
                                }
                            }
                        }
                        if (list.Count > 0)
                        {
                            OpenAiModels = list.OrderBy(x => x).ToList();
                            OnModelsUpdated?.Invoke("OpenAI", OpenAiModels);
                        }
                    }
                }
                catch (Exception ex)
                {
                    SendLogLine($"Warning: Could not fetch OpenAI models dynamically: {ex.Message}");
                }
            }

            var anthropicKey = _apiKeyStore.GetAnthropicApiKey();
            if (!string.IsNullOrEmpty(anthropicKey))
            {
                // Anthropic doesn't have a public List Models API yet.
                // We'll leave this empty for now as requested, letting users type their desired model.
            }
        }
        catch (Exception ex)
        {
            SendLogLine($"Warning: Dynamic model fetching failed: {ex.Message}");
        }
    }

    private async Task<string> ExecuteLinearHandoffChainAsync(string agentName, string task, CancellationToken ct)
    {
        var initialUserTask = task;
        string currentAgent = agentName;
        string currentTask = task;
        string lastResponse = string.Empty;

        while (currentAgent != OrchestratorAgentName && !ct.IsCancellationRequested)
        {
            if (!Agents.TryGetValue(currentAgent, out var activeAgent)) break;

            var stepStartContext = new AgentStepHookContext(LifecycleHookStage.AgentStepStart, currentAgent, currentTask);
            await RunLifecycleHooksAsync(stepStartContext, ct).ConfigureAwait(false);
            if (stepStartContext.IsBlocked)
                return $"Chain blocked by plugin hook at '{currentAgent}': {stepStartContext.BlockReason}";
            currentTask = stepStartContext.Task;

            OnAgentActivityChanged?.Invoke(currentAgent, AgentActivityKind.Thinking);
            BeginModelStreamBatch(currentAgent);
            OnAgentModelStreamStarted?.Invoke(currentAgent, AccentForSender(currentAgent));
            ChatResponse response;
            var hasReturnEdges = GetEffectiveWorkflowEdges().Any(e => string.Equals(e.From, currentAgent, StringComparison.OrdinalIgnoreCase) && e.Kind == Sharpwire.Core.Workflow.WorkflowEdgeKind.Return);
            try
            {
                response = await activeAgent.GetResponseAsync(
                    currentTask,
                    _session.Transcript.CopyForModelPrompt(),
                    ct,
                    d => AppendModelStreamBatch(currentAgent, d),
                    hasReturnEdges).ConfigureAwait(false);
            }
            finally
            {
                FlushModelStreamBatch(currentAgent);
                OnAgentModelStreamEnded?.Invoke(currentAgent);
            }
            lastResponse = LastNonEmptyAssistantText(response);
            var stepEndContext = new AgentStepHookContext(LifecycleHookStage.AgentStepEnd, currentAgent, currentTask)
            {
                ResponseText = lastResponse,
                ResponseUsedTools = ResponseUsedTools(response),
                CorrelationId = stepStartContext.CorrelationId
            };
            await RunLifecycleHooksAsync(stepEndContext, ct).ConfigureAwait(false);
            lastResponse = stepEndContext.ResponseText ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(lastResponse))
                EmitChatMessage(MessageRole.Agent, lastResponse, currentAgent, AccentForSender(currentAgent));
            if (stepEndContext.ResponseUsedTools)
            {
                OnAgentActivityChanged?.Invoke(currentAgent, AgentActivityKind.ToolUse);
                try
                {
                    await Task.Delay(320, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }

            OnAgentActivityChanged?.Invoke(currentAgent, AgentActivityKind.Idle);
            OnAgentStateChanged?.Invoke(currentAgent, AgentState.Idle);

            string next = activeAgent.Definition.NextAgentName;
            if (string.IsNullOrEmpty(next) || next == OrchestratorAgentName) break;

            OnAgentHandoffFlow?.Invoke(activeAgent.Definition.Name, next, false);
            currentAgent = next;
            var handoffSummary = string.IsNullOrWhiteSpace(lastResponse)
                ? "(no assistant text in the last reply)"
                : lastResponse;
            var handoffTask =
                $"Original task:\n{initialUserTask}\n\n{activeAgent.Definition.Name} finished with this result: {handoffSummary}\n\nPlease continue the work.";
            var handoffContext = new ChainHandoffHookContext(activeAgent.Definition.Name, next, handoffTask, false);
            await RunLifecycleHooksAsync(handoffContext, ct).ConfigureAwait(false);
            if (handoffContext.IsBlocked)
                break;
            currentTask = handoffContext.HandoffTask;
            if (!string.Equals(next, handoffContext.ToAgent, StringComparison.OrdinalIgnoreCase)
                && Agents.ContainsKey(handoffContext.ToAgent))
            {
                next = handoffContext.ToAgent;
            }
            OnAgentStateChanged?.Invoke(currentAgent, AgentState.Busy);
            OnAgentActivityChanged?.Invoke(currentAgent, AgentActivityKind.Thinking);
        }

        return lastResponse;
    }

    public async Task<string> ExecuteAgentAsync(string agentName, string task, CancellationToken ct)
    {
        if (IsHiddenSystemAgent(agentName))
            return $"Error: '{agentName}' is host-internal. Use {nameof(ReviewProposedSharpwireDesignAsync)} for design review.";

        if (!Agents.TryGetValue(agentName, out var agent))
        {
            return $"Error: Agent '{agentName}' not found. Available agents: {string.Join(", ", Agents.Keys)}";
        }

        var startHookContext = new AgentExecutionHookContext(LifecycleHookStage.AgentExecutionStart, agentName, task);
        await RunLifecycleHooksAsync(startHookContext, ct).ConfigureAwait(false);
        if (startHookContext.IsBlocked)
            return $"Execution blocked by plugin hook: {startHookContext.BlockReason}";
        task = startHookContext.Task;

        OnAgentStateChanged?.Invoke(agentName, AgentState.Busy);
        EmitChatMessage(MessageRole.Agent, $"Received task from Orchestrator: {task}", agentName, AccentForSender(agentName));
        OnAgentActivityChanged?.Invoke(agentName, AgentActivityKind.Thinking);

        try
        {
            try
            {
                var mafResult = await TryExecuteHandoffWorkflowAsync(agentName, task, ct).ConfigureAwait(false);
                if (mafResult is not null)
                {
                    var endHookContext = new AgentExecutionHookContext(LifecycleHookStage.AgentExecutionEnd, agentName, task)
                    {
                        ResponseText = mafResult
                    };
                    await RunLifecycleHooksAsync(endHookContext, ct).ConfigureAwait(false);
                    return endHookContext.ResponseText ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                SendLogLine($"Warning: MAF workflow failed; using linear handoff. {ex.Message}");
            }

            var linearResult = await ExecuteLinearHandoffChainAsync(agentName, task, ct).ConfigureAwait(false);
            var linearEndHookContext = new AgentExecutionHookContext(LifecycleHookStage.AgentExecutionEnd, agentName, task)
            {
                ResponseText = linearResult
            };
            await RunLifecycleHooksAsync(linearEndHookContext, ct).ConfigureAwait(false);
            return linearEndHookContext.ResponseText ?? string.Empty;
        }
        catch (Exception ex)
        {
            OnAgentActivityChanged?.Invoke(agentName, AgentActivityKind.Idle);
            OnAgentStateChanged?.Invoke(agentName, AgentState.Error);
            return $"Error executing agent chain: {ex.Message}";
        }
    }

    public async Task ProcessChatInput(string task, CancellationToken ct)
    {
        try
        {
            var turnStartHook = new OrchestratorTurnHookContext(LifecycleHookStage.OrchestratorTurnStart, task);
            await RunLifecycleHooksAsync(turnStartHook, ct).ConfigureAwait(false);
            if (turnStartHook.IsBlocked)
            {
                EmitChatMessage(MessageRole.System, $"Request blocked by plugin hook: {turnStartHook.BlockReason}", "System", null);
                return;
            }
            task = turnStartHook.Task;

            if (!Agents.TryGetValue(OrchestratorAgentName, out var orchestrator))
            {
                throw new InvalidOperationException("Orchestrator agent not found.");
            }

            OnAgentStateChanged?.Invoke(OrchestratorAgentName, AgentState.Busy);
            OnAgentActivityChanged?.Invoke(OrchestratorAgentName, AgentActivityKind.Thinking);

            _session.Transcript.AppendUser(task);

            BeginModelStreamBatch(OrchestratorAgentName);
            OnAgentModelStreamStarted?.Invoke(OrchestratorAgentName, AccentForSender(OrchestratorAgentName));
            ChatResponse response;
            try
            {
                response = await orchestrator.GetResponseAsync(
                    task,
                    _session.Transcript.CopyForModelPrompt(),
                    ct,
                    d => AppendModelStreamBatch(OrchestratorAgentName, d)).ConfigureAwait(false);
            }
            finally
            {
                FlushModelStreamBatch(OrchestratorAgentName);
                OnAgentModelStreamEnded?.Invoke(OrchestratorAgentName);
            }
            
            _session.Transcript.AppendMessages(response.Messages);

            var orchestratorReply = LastNonEmptyAssistantText(response);
            var responseUsedTools = ResponseUsedTools(response);
            if (TryDetectAgentOnlyToolNotFound(orchestratorReply, out var missingTool))
            {
                var recoveryNudge =
                    $"Internal correction: '{missingTool}' is agent-only and not directly callable by Orchestrator. " +
                    $"Retry this turn now by using DelegateToAgent (or TriggerChain_*) so an agent executes '{missingTool}'. " +
                    "Do not claim direct file edits by Orchestrator.";
                _session.Transcript.AppendMessages(new[]
                {
                    new ChatMessage(ChatRole.System, recoveryNudge)
                });

                SendLogLine($"System: Auto-recovering orchestrator turn after direct '{missingTool}' tool attempt.");
                response = await orchestrator.GetResponseAsync(
                        task,
                        _session.Transcript.CopyForModelPrompt(),
                        ct)
                    .ConfigureAwait(false);
                _session.Transcript.AppendMessages(response.Messages);
                orchestratorReply = LastNonEmptyAssistantText(response);
                responseUsedTools = responseUsedTools || ResponseUsedTools(response);
            }

            var turnEndHook = new OrchestratorTurnHookContext(LifecycleHookStage.OrchestratorTurnEnd, task)
            {
                ResponseText = orchestratorReply,
                ResponseUsedTools = responseUsedTools,
                CorrelationId = turnStartHook.CorrelationId
            };
            await RunLifecycleHooksAsync(turnEndHook, ct).ConfigureAwait(false);
            orchestratorReply = turnEndHook.ResponseText ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(orchestratorReply))
                EmitChatMessage(MessageRole.Agent, orchestratorReply, OrchestratorAgentName, AccentForSender(OrchestratorAgentName));

            if (turnEndHook.ResponseUsedTools)
            {
                OnAgentActivityChanged?.Invoke(OrchestratorAgentName, AgentActivityKind.ToolUse);
                try
                {
                    await Task.Delay(320, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }

            OnAgentActivityChanged?.Invoke(OrchestratorAgentName, AgentActivityKind.Idle);
            OnAgentStateChanged?.Invoke(OrchestratorAgentName, AgentState.Success);
        }
        catch (OperationCanceledException)
        {
            SendLogLine("Loop aborted by user.");
            EmitChatMessage(MessageRole.System, "Operation aborted by user.", "System", null);
            foreach (var key in Agents.Keys)
            {
                OnAgentActivityChanged?.Invoke(key, AgentActivityKind.Idle);
                OnAgentStateChanged?.Invoke(key, AgentState.Idle);
            }
        }
        catch (Exception ex)
        {
            SendLogLine($"Error in loop: {ex.Message}");
            EmitChatMessage(MessageRole.System, $"Error: {ex.Message}", "System", null);
            foreach (var key in Agents.Keys)
            {
                OnAgentActivityChanged?.Invoke(key, AgentActivityKind.Idle);
                OnAgentStateChanged?.Invoke(key, AgentState.Error);
            }
        }
    }

    /// <summary>Uses the configured host LLM to propose name, description, instructions, and tools from a single user-provided role title (e.g. menu &quot;Add agent&quot;).</summary>
    public async Task<AgentDefinition?> SuggestAgentDefinitionFromRoleAsync(string roleTitle, CancellationToken ct)
    {
        if (_chatClient == null || string.IsNullOrWhiteSpace(roleTitle))
            return null;

        var assignable = AvailableTools.Keys
            .Where(id => !id.Contains("TriggerChain_", StringComparison.OrdinalIgnoreCase))
            .Where(id => !IsOrchestratorOnlyToolId(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var toolListForPrompt = string.Join(", ", assignable);

        var sys =
            "You configure agents for the Sharpwire desktop app. The user supplies one short ROLE title only.\n" +
            "Reply with ONLY a single JSON object (no markdown fences, no commentary) with these keys:\n" +
            "- \"name\": agent display name, natural language with spaces (e.g. \"Security Auditor\"), never PascalCase or snake_case as the whole name.\n" +
            "- \"description\": one or two sentences for the graph and orchestrator.\n" +
            "- \"instructions\": full system prompt that agent will follow.\n" +
            "- \"enabledTools\": array of tool id strings; every id MUST be copied exactly from the allow-list below. Pick a sensible minimal set for the role (e.g. ReadFile, ListFiles for read-only work; add file-writing tools only if the role edits the workspace).\n\n" +
            "Allow-list (exact ids): " + toolListForPrompt;

        var userMsg = "Role title: " + roleTitle.Trim();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, sys),
            new(ChatRole.User, userMsg)
        };

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, new ChatOptions(), ct).ConfigureAwait(false);
            var text = LastNonEmptyAssistantText(response);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var json = ExtractFirstJsonObject(text);
            if (json == null)
                return null;

            var dto = JsonSerializer.Deserialize<LlmSuggestedAgentDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
                return null;

            var uniqueName = MakeUniqueAgentName(dto.Name.Trim());

            var enabled = new List<string>();
            if (dto.EnabledTools != null)
            {
                foreach (var raw in dto.EnabledTools)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;
                    var key = assignable.Find(k => k.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (key != null)
                        enabled.Add(key);
                }
            }

            if (enabled.Count == 0)
            {
                foreach (var fb in new[] { "ReadFile", "ListFiles" })
                {
                    var key = assignable.Find(k => k.Equals(fb, StringComparison.OrdinalIgnoreCase));
                    if (key != null)
                        enabled.Add(key);
                }
            }

            return new AgentDefinition
            {
                Name = uniqueName,
                Role = roleTitle.Trim(),
                Description = string.IsNullOrWhiteSpace(dto.Description)
                    ? $"{uniqueName} — {roleTitle.Trim()}."
                    : dto.Description.Trim(),
                Instructions = string.IsNullOrWhiteSpace(dto.Instructions)
                    ? $"You are {uniqueName}. Your role: {roleTitle.Trim()}. Help the user accordingly."
                    : dto.Instructions.Trim(),
                EnabledTools = enabled,
                AccentColor = PickNextAvailableAccentColor(),
                NextAgentName = OrchestratorAgentName,
                IsChainEntry = true
            };
        }
        catch (Exception ex)
        {
            SendLogLine($"SuggestAgentDefinitionFromRole failed: {ex.Message}");
            return null;
        }
    }

    private static bool IsOrchestratorOnlyToolId(string id) =>
        id.Equals(DelegateToAgentToolId, StringComparison.OrdinalIgnoreCase)
        || id.Equals(CreateNewAgentToolId, StringComparison.OrdinalIgnoreCase)
        || id.Equals(CreateAgentChainToolId, StringComparison.OrdinalIgnoreCase)
        || id.Equals(ReviewProposedSharpwireDesignToolId, StringComparison.OrdinalIgnoreCase);

    private static string? ExtractFirstJsonObject(string text)
    {
        var s = text.Trim();
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return s[start..(end + 1)];
    }

    private sealed class LlmSuggestedAgentDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Instructions { get; set; }
        public List<string>? EnabledTools { get; set; }
    }
}

public class MockChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new ChatClientMetadata("Mock");
    public void Dispose() { }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[Mock Response: LLM Provider or API Key not configured correctly in SETTINGS]")));
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

public class OrchestratorPlugin
{
    private readonly AgentService _agentService;
    private readonly SettingsManager _settings;

    public OrchestratorPlugin(AgentService agentService, SettingsManager settings)
    {
        _agentService = agentService;
        _settings = settings;
    }

    [System.ComponentModel.Description("Delegates a task to another agent by exact name and returns their reply text. When you summarize for the user, attribute by bare name only (e.g. Coder, Reviewer)—not 'the Coder agent'. Never claim their output as your own first-person work.")]
    public async Task<string> DelegateToAgent(
        [System.ComponentModel.Description("The exact name of the agent to delegate to (e.g., 'Coder', 'Reviewer').")] string agentName, 
        [System.ComponentModel.Description("The specific instructions or task for the agent to execute.")] string instructions)
    {
        _agentService.Log($"Orchestrator: Delegating to '{agentName}' with instructions: {instructions}");
        _agentService.RaiseAgentActivity(AgentService.OrchestratorAgentName, AgentActivityKind.ToolUse);
        try
        {
            return await Task.Run(async () =>
                    await _agentService.ExecuteAgentAsync(agentName, instructions, CancellationToken.None)
                        .ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _agentService.RaiseAgentActivity(AgentService.OrchestratorAgentName, AgentActivityKind.Thinking);
        }
    }

    [System.ComponentModel.Description("Creates a SINGLE new, standalone specialized expert and deploys it to the system. Use this ONLY when you need one new expert. If you need multiple agents to work together in a sequence, you MUST use 'CreateAgentChain' instead. Runs an internal design review before creating the agent.")]
    public async Task<string> CreateNewAgent(
        [System.ComponentModel.Description("The unique name for the new agent. You MUST use natural language with spaces (e.g., 'Product Manager', 'System Tester'). Never use PascalCase or underscores.")] string name,
        [System.ComponentModel.Description("The short role title for the agent (e.g., 'Release Engineer').")] string role,
        [System.ComponentModel.Description("A clear description of what this agent is an expert in.")] string description,
        [System.ComponentModel.Description("Detailed system instructions for the agent's behavior and goal.")] string instructions,
        [System.ComponentModel.Description("The list of tool IDs to give this agent access to (from the 'AVAILABLE TOOLS' list).")] List<string>? enabledTools = null)
    {
        if (!_settings.Load().AllowOrchestratorAgentCreation)
        {
            return "Creating new agents from the orchestrator is disabled in Settings. The user can enable “Allow orchestrator to create new agents when relevant” or add agents manually from the UI.";
        }

        var enabled = enabledTools ?? new List<string>();
        var proposalJson = JsonSerializer.Serialize(
            new
            {
                intent = AgentService.CreateNewAgentToolId,
                agent = new { name, role, description, instructions, enabledTools = enabled }
            },
            new JsonSerializerOptions { WriteIndented = true });

        string review;
        try
        {
            review = await _agentService.ReviewProposedSharpwireDesignAsync(
                    "Proposed new agent (not yet created):\n" + proposalJson,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            review = $"(Internal design review failed: {ex.Message}. Proceeding with creation.)";
        }

        var requestedName = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(requestedName))
            requestedName = "New Agent";
        var resolvedName = _agentService.MakeUniqueAgentName(requestedName);

        _agentService.Log($"Orchestrator: Creating new agent '{resolvedName}'...");
        _agentService.RaiseAgentActivity(AgentService.OrchestratorAgentName, AgentActivityKind.ToolUse);
        try
        {
            _agentService.AddAgent(new AgentDefinition
            {
                Name = resolvedName,
                Role = role,
                Description = description,
                Instructions = instructions,
                EnabledTools = enabled,
                IsChainEntry = true
            });
        }
        finally
        {
            _agentService.RaiseAgentActivity(AgentService.OrchestratorAgentName, AgentActivityKind.Thinking);
        }

        var renameNote = string.Equals(resolvedName, requestedName, StringComparison.Ordinal)
            ? string.Empty
            : $"\nName adjusted to avoid collision: '{requestedName}' -> '{resolvedName}'.";

        return "Internal design review:\n" + review + "\n\n" +
               $"Agent '{resolvedName}' successfully created and is now available for delegation." +
               renameNote;
    }

    [System.ComponentModel.Description("Creates an entire chain of multiple agents wired together into a workflow (a pipeline). Use this whenever the user's request requires more than one agent to work in sequence or with feedback loops. This is more efficient than creating agents one-by-one. Runs an internal design review before creating agents.")]
    public async Task<string> CreateAgentChain(
        [System.ComponentModel.Description("The list of agent definitions to create. For names, you MUST use natural language with spaces (e.g., 'Draft Writer', 'Quality Reviewer'). For a sequence (A -> B), set A's NextAgentName to 'B'. For a feedback loop (A -> B -> A), set A's NextAgentName to 'B', B's NextAgentName to 'Orchestrator', and add a 'Return' connection from B back to A in the 'connections' list. Loops are mandatory for any 'Reviewer' or 'Editor' agent.")] List<AgentDefinition> agents,
        [System.ComponentModel.Description("Optional additional workflow edges, specifically 'Return' paths for review loops (e.g., From='Reviewer', To='Coder', Kind='Return', ConditionRef='review_failed'). You MUST use this to wire back from a critic to a creator.")] List<WorkflowEdgeRecord>? connections = null)
    {
        if (!_settings.Load().AllowOrchestratorAgentCreation)
        {
            return "Creating new agents from the orchestrator is disabled in Settings.";
        }

        var plannedAgents = BuildUniqueChainAgents(agents ?? new List<AgentDefinition>(), out var renamedAgents);
        var plannedConnections = RemapConnections(connections, renamedAgents);

        var proposalJson = JsonSerializer.Serialize(
            new { intent = AgentService.CreateAgentChainToolId, agents = plannedAgents, connections = plannedConnections },
            new JsonSerializerOptions { WriteIndented = true });

        string review;
        try
        {
            review = await _agentService.ReviewProposedSharpwireDesignAsync(
                    "Proposed agent chain (not yet created):\n" + proposalJson,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            review = $"(Internal design review failed: {ex.Message}. Proceeding with creation.)";
        }

        var names = string.Join(", ", plannedAgents.Select(a => a.Name));
        _agentService.Log($"Orchestrator: Creating agent chain: {names}...");
        _agentService.RaiseAgentActivity(AgentService.OrchestratorAgentName, AgentActivityKind.ToolUse);

        try
        {
            foreach (var def in plannedAgents)
                _agentService.AddAgent(def);

            if (plannedConnections.Count > 0)
                _agentService.AddWorkflowEdges(plannedConnections);

            _agentService.EnsureWorkflowEdgesMaterialized();
        }
        finally
        {
            _agentService.RaiseAgentActivity(AgentService.OrchestratorAgentName, AgentActivityKind.Thinking);
        }

        var renameSummary = renamedAgents.Count == 0
            ? string.Empty
            : "\nName adjustments to avoid collisions:\n- " +
              string.Join("\n- ", renamedAgents.Select(kv => $"{kv.Key} -> {kv.Value}"));

        return "Internal design review:\n" + review + "\n\n" +
               $"Successfully created agent chain with {plannedAgents.Count} agents: {names}." +
               renameSummary;
    }

    private List<AgentDefinition> BuildUniqueChainAgents(List<AgentDefinition> agents, out Dictionary<string, string> renamed)
    {
        renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existing = new HashSet<string>(_agentService.Agents.Keys, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var result = new List<AgentDefinition>();

        foreach (var def in agents)
        {
            var originalName = (def.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(originalName))
                originalName = "New Agent";

            var resolvedName = ResolveUniqueName(originalName, used);
            if (!originalName.Equals(resolvedName, StringComparison.OrdinalIgnoreCase))
                renamed[originalName] = resolvedName;

            result.Add(new AgentDefinition
            {
                Name = resolvedName,
                Role = def.Role,
                Description = def.Description,
                Instructions = def.Instructions,
                NextAgentName = def.NextAgentName,
                EnabledTools = def.EnabledTools?.ToList() ?? new List<string>(),
                AccentColor = def.AccentColor,
                IsChainEntry = def.IsChainEntry,
                JsonSchema = def.JsonSchema,
                LlmProvider = def.LlmProvider,
                ModelId = def.ModelId
            });
        }

        for (var i = 0; i < result.Count; i++)
        {
            var next = result[i].NextAgentName;
            if (!string.IsNullOrWhiteSpace(next) && renamed.TryGetValue(next, out var remapped))
            {
                var current = result[i];
                result[i] = new AgentDefinition
                {
                    Name = current.Name,
                    Role = current.Role,
                    Description = current.Description,
                    Instructions = current.Instructions,
                    NextAgentName = remapped,
                    EnabledTools = current.EnabledTools?.ToList() ?? new List<string>(),
                    AccentColor = current.AccentColor,
                    IsChainEntry = current.IsChainEntry,
                    JsonSchema = current.JsonSchema,
                    LlmProvider = current.LlmProvider,
                    ModelId = current.ModelId
                };
            }
        }

        return result;
    }

    private static List<WorkflowEdgeRecord> RemapConnections(List<WorkflowEdgeRecord>? connections, IReadOnlyDictionary<string, string> renamed)
    {
        if (connections == null || connections.Count == 0)
            return new List<WorkflowEdgeRecord>();

        return connections.Select(edge => new WorkflowEdgeRecord
        {
            From = renamed.TryGetValue(edge.From, out var fromRemapped) ? fromRemapped : edge.From,
            To = renamed.TryGetValue(edge.To, out var toRemapped) ? toRemapped : edge.To,
            Kind = edge.Kind,
            Label = edge.Label,
            ConditionRef = edge.ConditionRef,
            HitlTarget = edge.HitlTarget
        }).ToList();
    }

    private static string ResolveUniqueName(string baseName, HashSet<string> used)
    {
        var candidate = baseName;
        var suffix = 2;
        while (used.Contains(candidate))
        {
            candidate = $"{baseName} {suffix}";
            suffix++;
        }

        used.Add(candidate);
        return candidate;
    }

    [System.ComponentModel.Description("Runs Sharpwire's internal design reviewer on arbitrary text: planned agent definitions, plugin C# drafts, workflow edges, or configuration notes. Does not modify the system. Use before delegating plugin work to Coder or to double-check a plan.")]
    public async Task<string> ReviewProposedSharpwireDesign(
        [System.ComponentModel.Description("Full proposal or draft to review.")] string proposal)
    {
        _agentService.Log("Orchestrator: ReviewProposedSharpwireDesign");
        _agentService.RaiseAgentActivity(AgentService.OrchestratorAgentName, AgentActivityKind.ToolUse);
        try
        {
            return await _agentService.ReviewProposedSharpwireDesignAsync(proposal, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _agentService.RaiseAgentActivity(AgentService.OrchestratorAgentName, AgentActivityKind.Thinking);
        }
    }
}

public class SharpwireDocumentationPlugin
{
    private readonly AgentService _agentService;

    public SharpwireDocumentationPlugin(AgentService agentService)
    {
        _agentService = agentService;
    }

    [System.ComponentModel.Description("Answers questions about Sharpwire host behavior and plugin/hook APIs using built-in documentation references.")]
    public string AskSharpwireDocumentation(
        [System.ComponentModel.Description("A specific question about Sharpwire APIs, lifecycle hooks, plugin construction, settings, or tool approval.")] string question)
    {
        return _agentService.GetSharpwireDocumentationAnswer(question);
    }
}
