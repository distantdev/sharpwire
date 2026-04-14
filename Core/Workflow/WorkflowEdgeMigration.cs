using System;
using System.Collections.Generic;
using System.Linq;
using Sharpwire.Core.Agents;

namespace Sharpwire.Core.Workflow;

public static class WorkflowEdgeMigration
{
    /// <summary>
    /// Persisted edges reconciled with each agent's <see cref="AgentDefinition.NextAgentName"/> for
    /// <see cref="WorkflowEdgeKind.Default"/> handoffs. Stale rows in <c>workflow-edges.json</c> (e.g. Poet→Orchestrator)
    /// no longer override an updated next target (e.g. Reviewer) from the graph or agent editor.
    /// When the next target is Orchestrator (or unset), a default edge to <c>Orchestrator</c> is synthesized so the
    /// MAF graph reaches the workflow end node. Return edges and other kinds are left unchanged.
    /// YAML <c>return_logic</c> merges still apply on top of the stored list.
    /// </summary>
    public static IReadOnlyList<WorkflowEdgeRecord> MergeWithAgentDefinitions(
        IReadOnlyList<WorkflowEdgeRecord> existing,
        IEnumerable<Agent> agents)
    {
        var list = existing.ToList();
        var agentList = agents as IList<Agent> ?? agents.ToList();

        var names = new HashSet<string>(
            agentList.Select(a => (a.Definition.Name ?? string.Empty).Trim())
                     .Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var agent in agentList)
        {
            var name = (agent.Definition.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)
                || name.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase)
                || AgentService.IsHiddenSystemAgent(name))
                continue;

            var next = agent.Definition.NextAgentName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(next)
                || next.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAll(e =>
                    string.Equals(e.From, name, StringComparison.OrdinalIgnoreCase)
                    && e.Kind == WorkflowEdgeKind.Default);
                list.Add(new WorkflowEdgeRecord
                {
                    From = name,
                    To = "Orchestrator",
                    Kind = WorkflowEdgeKind.Default,
                    ConditionRef = null,
                    Label = null
                });
                continue;
            }

            if (!names.Contains(next))
                continue;

            var existingDefault = list.FirstOrDefault(e =>
                string.Equals(e.From, name, StringComparison.OrdinalIgnoreCase)
                && e.Kind == WorkflowEdgeKind.Default);
            if (existingDefault != null
                && string.Equals(existingDefault.To, next, StringComparison.OrdinalIgnoreCase))
                continue;

            list.RemoveAll(e =>
                string.Equals(e.From, name, StringComparison.OrdinalIgnoreCase)
                && e.Kind == WorkflowEdgeKind.Default);
            list.Add(new WorkflowEdgeRecord
            {
                From = name,
                To = next,
                Kind = WorkflowEdgeKind.Default,
                ConditionRef = null,
                Label = null
            });
        }

        return list;
    }

    /// <summary>True when the two lists represent the same multiset of edges (order-independent).</summary>
    public static bool EquivalentEdgeSets(
        IReadOnlyList<WorkflowEdgeRecord> a,
        IReadOnlyList<WorkflowEdgeRecord> b)
    {
        if (a.Count != b.Count)
            return false;

        static string Key(WorkflowEdgeRecord e) =>
            $"{e.From}\u001f{e.To}\u001f{e.Kind}\u001f{e.Label ?? ""}\u001f{e.ConditionRef ?? ""}\u001f{e.HitlTarget ?? ""}";

        var sa = a.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var sb = b.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return sa.SequenceEqual(sb, StringComparer.Ordinal);
    }
}
