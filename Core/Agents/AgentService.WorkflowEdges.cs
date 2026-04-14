using System;
using System.Collections.Generic;
using System.Linq;
using Sharpwire.Core.Workflow;

namespace Sharpwire.Core.Agents;

public partial class AgentService
{
    private List<WorkflowEdgeRecord> LoadWorkflowEdgesMutable() =>
        _session.State.LoadWorkflowEdges().ToList();

    private void SaveWorkflowEdgesAndNotify(IReadOnlyList<WorkflowEdgeRecord> edges)
    {
        var list = edges is List<WorkflowEdgeRecord> l ? l : edges.ToList();
        var cleaned = Agents.Count > 0
            ? WorkflowEdgeSanitizer.Sanitize(list, Agents.Keys)
            : WorkflowEdgeSanitizer.SanitizeStructuralOnly(list);
        _session.State.SaveWorkflowEdges(cleaned);
        OnHandoffTopologyChanged?.Invoke();
    }

    /// <summary>
    /// Ensures <c>workflow-edges.json</c> includes default handoff edges implied by <see cref="AgentDefinition.NextAgentName"/>
    /// for any agent that has no default edge yet (e.g. after YAML return_logic wrote Return-only rows).
    /// </summary>
    public void EnsureWorkflowEdgesMaterialized()
    {
        var stored = _session.State.LoadWorkflowEdges().ToList();
        var merged = WorkflowEdgeMigration.MergeWithAgentDefinitions(stored, Agents.Values).ToList();
        if (merged.Count == 0)
            return;
        if (WorkflowEdgeMigration.EquivalentEdgeSets(stored, merged))
            return;
        SaveWorkflowEdgesAndNotify(merged);
    }

    public IReadOnlyList<WorkflowEdgeRecord> GetEffectiveWorkflowEdges() =>
        WorkflowEdgeMigration.MergeWithAgentDefinitions(_session.State.LoadWorkflowEdges(), Agents.Values);

    public void AddWorkflowEdges(IEnumerable<WorkflowEdgeRecord> edges)
    {
        var current = LoadWorkflowEdgesMutable();
        current.AddRange(edges);
        SaveWorkflowEdgesAndNotify(current);
    }

    public void SetReturnHandoffTarget(string fromAgent, string? toAgent)
    {
        if (_chatClient == null
            || string.IsNullOrWhiteSpace(fromAgent)
            || fromAgent.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase))
            return;
        if (!Agents.ContainsKey(fromAgent))
            return;

        var edges = LoadWorkflowEdgesMutable();
        edges.RemoveAll(e => string.Equals(e.From, fromAgent, StringComparison.OrdinalIgnoreCase)
                             && e.Kind == WorkflowEdgeKind.Return);

        if (!string.IsNullOrWhiteSpace(toAgent)
            && !toAgent.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase)
            && Agents.ContainsKey(toAgent.Trim()))
        {
            edges.Add(new WorkflowEdgeRecord
            {
                From = fromAgent,
                To = toAgent.Trim(),
                Kind = WorkflowEdgeKind.Return,
                Label = "Retry with critique",
                ConditionRef = "review_failed"
            });
        }

        SaveWorkflowEdgesAndNotify(edges);
    }

    public void UpdateWorkflowEdgeProperties(string fromAgent, string toAgent, bool isReturn, string? label, string? conditionRef)
    {
        var edges = LoadWorkflowEdgesMutable();
        var kind = isReturn ? WorkflowEdgeKind.Return : WorkflowEdgeKind.Default;
        var edge = edges.FirstOrDefault(e => 
            string.Equals(e.From, fromAgent, StringComparison.OrdinalIgnoreCase) && 
            string.Equals(e.To, toAgent, StringComparison.OrdinalIgnoreCase) &&
            e.Kind == kind);

        if (edge != null)
        {
            edge.Label = label;
            edge.ConditionRef = conditionRef;
            SaveWorkflowEdgesAndNotify(edges);
        }
    }

    private void RenameWorkflowEdgeEndpoints(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            return;
        var edges = LoadWorkflowEdgesMutable();
        var changed = false;
        foreach (var e in edges)
        {
            if (string.Equals(e.From, oldName, StringComparison.OrdinalIgnoreCase))
            {
                e.From = newName;
                changed = true;
            }

            if (string.Equals(e.To, oldName, StringComparison.OrdinalIgnoreCase))
            {
                e.To = newName;
                changed = true;
            }

            if (e.HitlTarget != null && string.Equals(e.HitlTarget, oldName, StringComparison.OrdinalIgnoreCase))
            {
                e.HitlTarget = newName;
                changed = true;
            }
        }

        if (changed)
            SaveWorkflowEdgesAndNotify(edges);
    }

    private void PruneWorkflowEdgesForRemovedAgent(string removedName)
    {
        var edges = LoadWorkflowEdgesMutable();
        var n = edges.RemoveAll(e =>
            string.Equals(e.From, removedName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.To, removedName, StringComparison.OrdinalIgnoreCase)
            || (e.HitlTarget != null && string.Equals(e.HitlTarget, removedName, StringComparison.OrdinalIgnoreCase)));
        if (n > 0)
            SaveWorkflowEdgesAndNotify(edges);
    }

    private void SyncDefaultWorkflowEdgeWithHandoff(string fromAgent, string nextTarget)
    {
        var edges = LoadWorkflowEdgesMutable();
        edges.RemoveAll(e => string.Equals(e.From, fromAgent, StringComparison.OrdinalIgnoreCase)
                             && e.Kind == WorkflowEdgeKind.Default);
        if (!string.IsNullOrWhiteSpace(nextTarget)
            && !nextTarget.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase))
        {
            edges.Add(new WorkflowEdgeRecord
            {
                From = fromAgent,
                To = nextTarget,
                Kind = WorkflowEdgeKind.Default
            });
        }

        SaveWorkflowEdgesAndNotify(edges);
    }

    private void PruneInvalidPersistedWorkflowEdges()
    {
        if (Agents.Count == 0)
            return;

        var stored = _session.State.LoadWorkflowEdges().ToList();
        var cleaned = WorkflowEdgeSanitizer.Sanitize(stored, Agents.Keys);
        if (WorkflowEdgeMigration.EquivalentEdgeSets(stored, cleaned))
            return;

        SaveWorkflowEdgesAndNotify(cleaned);
        SendLogLine("System: Removed invalid workflow edges (unknown agents or empty endpoints).");
    }
}
