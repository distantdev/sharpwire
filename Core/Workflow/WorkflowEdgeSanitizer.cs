using System;
using System.Collections.Generic;
using System.Linq;

namespace Sharpwire.Core.Workflow;

/// <summary>Trims workflow edge strings and drops invalid or dangling rows.</summary>
public static class WorkflowEdgeSanitizer
{
    public static List<WorkflowEdgeRecord> SanitizeStructuralOnly(IReadOnlyList<WorkflowEdgeRecord> edges)
    {
        var list = new List<WorkflowEdgeRecord>();
        foreach (var e in edges)
        {
            var from = (e.From ?? string.Empty).Trim();
            var to = (e.To ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
                continue;

            var hitl = string.IsNullOrWhiteSpace(e.HitlTarget) ? null : e.HitlTarget.Trim();

            list.Add(new WorkflowEdgeRecord
            {
                From = from,
                To = to,
                Kind = e.Kind,
                Label = string.IsNullOrWhiteSpace(e.Label) ? null : e.Label.Trim(),
                ConditionRef = string.IsNullOrWhiteSpace(e.ConditionRef) ? null : e.ConditionRef.Trim(),
                HitlTarget = string.IsNullOrEmpty(hitl) ? null : hitl
            });
        }

        return list;
    }

    public static List<WorkflowEdgeRecord> Sanitize(
        IReadOnlyList<WorkflowEdgeRecord> edges,
        IReadOnlyCollection<string> validAgentNames)
    {
        var valid = new HashSet<string>(
            validAgentNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var list = new List<WorkflowEdgeRecord>();
        foreach (var e in edges)
        {
            var from = (e.From ?? string.Empty).Trim();
            var to = (e.To ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
                continue;

            if (!valid.Contains(from) || !valid.Contains(to))
                continue;

            string? hitl = string.IsNullOrWhiteSpace(e.HitlTarget) ? null : e.HitlTarget.Trim();
            if (hitl != null && !valid.Contains(hitl))
                hitl = null;

            list.Add(new WorkflowEdgeRecord
            {
                From = from,
                To = to,
                Kind = e.Kind,
                Label = string.IsNullOrWhiteSpace(e.Label) ? null : e.Label.Trim(),
                ConditionRef = string.IsNullOrWhiteSpace(e.ConditionRef) ? null : e.ConditionRef.Trim(),
                HitlTarget = hitl
            });
        }

        return list;
    }
}
