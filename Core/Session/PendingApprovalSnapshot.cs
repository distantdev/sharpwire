namespace Sharpwire.Core.Session;

/// <summary>Serializable pending HITL approval (STANDARDS §5); resume after app restart.</summary>
public sealed record PendingApprovalSnapshot(
    string ToolId,
    string Summary,
    string? ContextJson,
    string UtcCreatedIso);
