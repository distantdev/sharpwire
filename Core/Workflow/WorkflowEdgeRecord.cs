using System.Text.Json.Serialization;

namespace Sharpwire.Core.Workflow;

public enum WorkflowEdgeKind
{
    Default,
    Return
}

/// <summary>Persisted Nodify / YAML edge mapped to MAF WorkflowBuilder.AddEdge.</summary>
public sealed class WorkflowEdgeRecord
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorkflowEdgeKind Kind { get; set; } = WorkflowEdgeKind.Default;

    /// <summary>Optional visualization label (MAF edge label).</summary>
    public string? Label { get; set; }

    /// <summary>Logical condition: null or "always" = unconditional; "review_failed" / "review_passed" drive predicates when payload carries <see cref="WorkflowReviewResult"/>.</summary>
    public string? ConditionRef { get; set; }

    /// <summary>Optional explicit HITL target agent when tripwire fires (otherwise <c>__hitl</c> sink).</summary>
    public string? HitlTarget { get; set; }
}
