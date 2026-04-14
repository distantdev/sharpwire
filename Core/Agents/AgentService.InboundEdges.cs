using System;
using System.Linq;
using Sharpwire.Core.Workflow;

namespace Sharpwire.Core.Agents;

public partial class AgentService
{
    /// <summary>Removes default (happy-path) edges whose target is <paramref name="toAgent"/> (disconnect on <c>In</c> port).</summary>
    public void RemoveInboundDefaultEdgesTo(string toAgent)
    {
        if (string.IsNullOrWhiteSpace(toAgent))
            return;
        foreach (var e in GetEffectiveWorkflowEdges()
                     .Where(e => e.Kind == WorkflowEdgeKind.Default
                                 && string.Equals(e.To, toAgent, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            SetHandoffTarget(e.From, "Orchestrator");
        }
    }

    /// <summary>Removes return edges whose target is <paramref name="toAgent"/> (disconnect on <c>Feedback</c> input port).</summary>
    public void RemoveInboundReturnEdgesTo(string toAgent)
    {
        if (string.IsNullOrWhiteSpace(toAgent))
            return;
        foreach (var e in GetEffectiveWorkflowEdges()
                     .Where(e => e.Kind == WorkflowEdgeKind.Return
                                 && string.Equals(e.To, toAgent, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            SetReturnHandoffTarget(e.From, null);
        }
    }
}
