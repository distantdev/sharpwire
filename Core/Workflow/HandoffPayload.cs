namespace Sharpwire.Core.Workflow;

/// <summary>Payload routed across MAF conditional edges (matches workflow superstep message typing).</summary>
public sealed class HandoffPayload
{
    public string InitialUserTask { get; init; } = string.Empty;
    public string CurrentTask { get; init; } = string.Empty;
    public string LastAgent { get; init; } = string.Empty;
    public string LastAssistantText { get; init; } = string.Empty;
    public string? JsonOutput { get; init; }
    public WorkflowReviewResult? Review { get; init; }
    public bool TripwireHit { get; init; }

    public HandoffPayload WithRunResult(
        string agentName,
        string assistantText,
        string? jsonOutput,
        WorkflowReviewResult? review,
        bool tripwireHit) =>
        new()
        {
            InitialUserTask = InitialUserTask,
            CurrentTask = CurrentTask,
            LastAgent = agentName,
            LastAssistantText = assistantText,
            JsonOutput = jsonOutput,
            Review = review,
            TripwireHit = tripwireHit
        };
}

public sealed class WorkflowReviewResult
{
    public string Status { get; set; } = string.Empty;
    public string? Critique { get; set; }
}
