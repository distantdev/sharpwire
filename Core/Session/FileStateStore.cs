using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sharpwire.Core;
using Sharpwire.Core.Agents;
using Sharpwire.Core.Workflow;

namespace Sharpwire.Core.Session;

public sealed class FileStateStore : IStateStore
{
    private readonly string _sharpwireDir;
    private readonly string _agentsPath;
    private readonly string _layoutPath;
    private readonly string _pendingApprovalPath;
    private readonly string _handoffOverridesPath;
    private readonly string _workflowEdgesPath;
    private readonly string _workflowRunStatePath;
    private readonly string _scenesDir;
    private readonly object _sync = new();
    private readonly Dictionary<string, NodePosition> _layouts = new(StringComparer.OrdinalIgnoreCase);
    private bool _layoutsLoaded;
    private readonly Dictionary<string, string> _handoffOverrides = new(StringComparer.OrdinalIgnoreCase);
    private bool _handoffsLoaded;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FileStateStore(AppPaths paths)
    {
        _sharpwireDir = Path.Combine(paths.WorkspaceDirectory, ".sharpwire");
        _agentsPath = Path.Combine(_sharpwireDir, "agents.json");
        _layoutPath = Path.Combine(_sharpwireDir, "graph-layout.json");
        _pendingApprovalPath = Path.Combine(_sharpwireDir, "pending-approval.json");
        _handoffOverridesPath = Path.Combine(_sharpwireDir, "handoff-overrides.json");
        _workflowEdgesPath = Path.Combine(_sharpwireDir, "workflow-edges.json");
        _workflowRunStatePath = Path.Combine(_sharpwireDir, "workflow-run-state.json");
        _scenesDir = Path.Combine(_sharpwireDir, "scenes");
    }

    private void EnsureDir()
    {
        if (!Directory.Exists(_sharpwireDir))
            Directory.CreateDirectory(_sharpwireDir);
    }

    private void EnsureLayoutsLoaded()
    {
        lock (_sync)
        {
            if (_layoutsLoaded)
                return;
            _layoutsLoaded = true;
            if (!File.Exists(_layoutPath))
                return;
            try
            {
                var json = File.ReadAllText(_layoutPath);
                var dto = JsonSerializer.Deserialize<LayoutFileDto>(json, JsonOpts);
                if (dto?.Nodes == null)
                    return;
                foreach (var kv in dto.Nodes)
                {
                    if (kv.Value is null)
                        continue;
                    _layouts[kv.Key] = new NodePosition(kv.Value.X, kv.Value.Y);
                }
            }
            catch
            {
                /* ignore corrupt layout */
            }
        }
    }

    private void FlushLayoutsLocked()
    {
        EnsureDir();
        var dto = new LayoutFileDto
        {
            Nodes = _layouts.ToDictionary(kv => kv.Key, kv => new LayoutPointDto { X = kv.Value.X, Y = kv.Value.Y })
        };
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        File.WriteAllText(_layoutPath, json);
    }

    public IReadOnlyList<AgentDefinition> LoadCustomAgentDefinitions()
    {
        if (!File.Exists(_agentsPath))
            return Array.Empty<AgentDefinition>();
        try
        {
            var json = File.ReadAllText(_agentsPath);
            var list = JsonSerializer.Deserialize<List<AgentDefinition>>(json, JsonOpts);
            return list ?? (IReadOnlyList<AgentDefinition>)Array.Empty<AgentDefinition>();
        }
        catch
        {
            return Array.Empty<AgentDefinition>();
        }
    }

    public void SaveCustomAgentDefinitions(IReadOnlyList<AgentDefinition> definitions)
    {
        EnsureDir();
        var json = JsonSerializer.Serialize(definitions.ToList(), JsonOpts);
        File.WriteAllText(_agentsPath, json);
    }

    public bool TryGetNodeLayout(string agentName, out NodePosition position)
    {
        EnsureLayoutsLoaded();
        lock (_sync)
            return _layouts.TryGetValue(agentName, out position);
    }

    public void SetNodeLayout(string agentName, in NodePosition position)
    {
        EnsureLayoutsLoaded();
        lock (_sync)
        {
            _layouts[agentName] = position;
            FlushLayoutsLocked();
        }
    }

    public void RemoveNodeLayout(string agentName)
    {
        EnsureLayoutsLoaded();
        lock (_sync)
        {
            if (_layouts.Remove(agentName))
                FlushLayoutsLocked();
        }
    }

    public void RenameNodeLayout(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            return;
        EnsureLayoutsLoaded();
        lock (_sync)
        {
            if (!_layouts.Remove(oldName, out var pos))
                return;
            _layouts[newName] = pos;
            FlushLayoutsLocked();
        }
    }

    private sealed class LayoutFileDto
    {
        public Dictionary<string, LayoutPointDto>? Nodes { get; set; }
    }

    private sealed class LayoutPointDto
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    public PendingApprovalSnapshot? LoadPendingApproval()
    {
        lock (_sync)
        {
            if (!File.Exists(_pendingApprovalPath))
                return null;
            try
            {
                var json = File.ReadAllText(_pendingApprovalPath);
                return JsonSerializer.Deserialize<PendingApprovalSnapshot>(json, JsonOpts);
            }
            catch
            {
                return null;
            }
        }
    }

    public void SavePendingApproval(PendingApprovalSnapshot? snapshot)
    {
        lock (_sync)
        {
            EnsureDir();
            if (snapshot == null)
            {
                if (File.Exists(_pendingApprovalPath))
                    File.Delete(_pendingApprovalPath);
                return;
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOpts);
            File.WriteAllText(_pendingApprovalPath, json);
        }
    }

    private void EnsureHandoffsLoaded()
    {
        lock (_sync)
        {
            if (_handoffsLoaded)
                return;
            _handoffsLoaded = true;
            if (!File.Exists(_handoffOverridesPath))
                return;
            try
            {
                var json = File.ReadAllText(_handoffOverridesPath);
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts);
                if (map == null)
                    return;
                foreach (var kv in map)
                    _handoffOverrides[kv.Key] = kv.Value;
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private void FlushHandoffsLocked()
    {
        EnsureDir();
        var json = JsonSerializer.Serialize(_handoffOverrides, JsonOpts);
        File.WriteAllText(_handoffOverridesPath, json);
    }

    public IReadOnlyDictionary<string, string> LoadHandoffOverrides()
    {
        EnsureHandoffsLoaded();
        lock (_sync)
            return new Dictionary<string, string>(_handoffOverrides, StringComparer.OrdinalIgnoreCase);
    }

    public void SetHandoffOverride(string fromAgent, string? toAgent)
    {
        EnsureHandoffsLoaded();
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(toAgent)
                || string.Equals(toAgent, "Orchestrator", StringComparison.OrdinalIgnoreCase))
                _handoffOverrides.Remove(fromAgent);
            else
                _handoffOverrides[fromAgent] = toAgent.Trim();
            FlushHandoffsLocked();
        }
    }

    public void RemoveHandoffOverride(string fromAgent)
    {
        EnsureHandoffsLoaded();
        lock (_sync)
        {
            if (_handoffOverrides.Remove(fromAgent))
                FlushHandoffsLocked();
        }
    }

    public void RenameHandoffOverrideKey(string oldFromName, string newFromName)
    {
        if (string.Equals(oldFromName, newFromName, StringComparison.OrdinalIgnoreCase))
            return;
        EnsureHandoffsLoaded();
        lock (_sync)
        {
            if (!_handoffOverrides.Remove(oldFromName, out var to))
                return;
            _handoffOverrides[newFromName] = to;
            FlushHandoffsLocked();
        }
    }

    public void RenameHandoffTarget(string oldTargetName, string newTargetName)
    {
        EnsureHandoffsLoaded();
        lock (_sync)
        {
            var changed = false;
            foreach (var key in _handoffOverrides.Keys.ToList())
            {
                if (!string.Equals(_handoffOverrides[key], oldTargetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                _handoffOverrides[key] = newTargetName;
                changed = true;
            }

            if (changed)
                FlushHandoffsLocked();
        }
    }

    public void RemoveHandoffReferencesToAgent(string removedAgentName)
    {
        EnsureHandoffsLoaded();
        lock (_sync)
        {
            var changed = _handoffOverrides.Remove(removedAgentName);
            foreach (var key in _handoffOverrides.Keys.ToList())
            {
                if (!string.Equals(_handoffOverrides[key], removedAgentName, StringComparison.OrdinalIgnoreCase))
                    continue;
                _handoffOverrides[key] = "Orchestrator";
                changed = true;
            }

            if (changed)
                FlushHandoffsLocked();
        }
    }

    public IReadOnlyList<WorkflowEdgeRecord> LoadWorkflowEdges()
    {
        lock (_sync)
        {
            if (!File.Exists(_workflowEdgesPath))
                return Array.Empty<WorkflowEdgeRecord>();
            try
            {
                var json = File.ReadAllText(_workflowEdgesPath);
                var dto = JsonSerializer.Deserialize<WorkflowEdgesFileDto>(json, JsonOpts);
                if (dto?.Edges == null || dto.Edges.Count == 0)
                    return Array.Empty<WorkflowEdgeRecord>();
                return WorkflowEdgeSanitizer.SanitizeStructuralOnly(dto.Edges);
            }
            catch
            {
                return Array.Empty<WorkflowEdgeRecord>();
            }
        }
    }

    public void SaveWorkflowEdges(IReadOnlyList<WorkflowEdgeRecord> edges)
    {
        lock (_sync)
        {
            EnsureDir();
            var cleaned = WorkflowEdgeSanitizer.SanitizeStructuralOnly(edges);
            var dto = new WorkflowEdgesFileDto { Edges = cleaned };
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            File.WriteAllText(_workflowEdgesPath, json);
        }
    }

    public WorkflowRunStateDto LoadWorkflowRunState()
    {
        lock (_sync)
        {
            if (!File.Exists(_workflowRunStatePath))
                return new WorkflowRunStateDto();
            try
            {
                var json = File.ReadAllText(_workflowRunStatePath);
                return JsonSerializer.Deserialize<WorkflowRunStateDto>(json, JsonOpts) ?? new WorkflowRunStateDto();
            }
            catch
            {
                return new WorkflowRunStateDto();
            }
        }
    }

    public void SaveWorkflowRunState(WorkflowRunStateDto state)
    {
        lock (_sync)
        {
            EnsureDir();
            var json = JsonSerializer.Serialize(state ?? new WorkflowRunStateDto(), JsonOpts);
            File.WriteAllText(_workflowRunStatePath, json);
        }
    }

    public void ClearAllData()
    {
        lock (_sync)
        {
            if (File.Exists(_agentsPath)) File.Delete(_agentsPath);
            if (File.Exists(_layoutPath)) File.Delete(_layoutPath);
            if (File.Exists(_handoffOverridesPath)) File.Delete(_handoffOverridesPath);
            if (File.Exists(_workflowEdgesPath)) File.Delete(_workflowEdgesPath);
            if (File.Exists(_workflowRunStatePath)) File.Delete(_workflowRunStatePath);
            _layouts.Clear();
            _layoutsLoaded = false;
            _handoffOverrides.Clear();
            _handoffsLoaded = false;
        }
    }

    public void SaveScene(string sceneName)
    {
        lock (_sync)
        {
            var targetDir = Path.Combine(_scenesDir, sceneName);
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            if (File.Exists(_agentsPath)) File.Copy(_agentsPath, Path.Combine(targetDir, "agents.json"), true);
            if (File.Exists(_layoutPath)) File.Copy(_layoutPath, Path.Combine(targetDir, "graph-layout.json"), true);
            if (File.Exists(_handoffOverridesPath)) File.Copy(_handoffOverridesPath, Path.Combine(targetDir, "handoff-overrides.json"), true);
            if (File.Exists(_workflowEdgesPath)) File.Copy(_workflowEdgesPath, Path.Combine(targetDir, "workflow-edges.json"), true);
        }
    }

    public void LoadScene(string sceneName)
    {
        lock (_sync)
        {
            var sourceDir = Path.Combine(_scenesDir, sceneName);
            if (!Directory.Exists(sourceDir)) return;

            ClearAllData();
            EnsureDir();

            if (File.Exists(Path.Combine(sourceDir, "agents.json"))) File.Copy(Path.Combine(sourceDir, "agents.json"), _agentsPath, true);
            if (File.Exists(Path.Combine(sourceDir, "graph-layout.json"))) File.Copy(Path.Combine(sourceDir, "graph-layout.json"), _layoutPath, true);
            if (File.Exists(Path.Combine(sourceDir, "handoff-overrides.json"))) File.Copy(Path.Combine(sourceDir, "handoff-overrides.json"), _handoffOverridesPath, true);
            if (File.Exists(Path.Combine(sourceDir, "workflow-edges.json"))) File.Copy(Path.Combine(sourceDir, "workflow-edges.json"), _workflowEdgesPath, true);
        }
    }

    public IReadOnlyList<string> GetSceneNames()
    {
        if (!Directory.Exists(_scenesDir)) return Array.Empty<string>();
        return Directory.GetDirectories(_scenesDir).Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList();
    }

    public void Reload()
    {
        lock (_sync)
        {
            _layouts.Clear();
            _layoutsLoaded = false;
            _handoffOverrides.Clear();
            _handoffsLoaded = false;
        }
    }
}
