using System.Collections.Generic;

namespace Sharpwire.Core.Workflow;

/// <summary>Persisted loop / HITL bookkeeping for workflow runs (resume across restarts).</summary>
public sealed class WorkflowRunStateDto
{
    /// <summary>Key = loop id (e.g. Reviewer_return_Coder), value = times return wire was taken.</summary>
    public Dictionary<string, int> LoopIterations { get; set; } = new();

    public string? LastRunSessionId { get; set; }

    public bool PausedForHitl { get; set; }

    public string? HitlSummary { get; set; }
}
