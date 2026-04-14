namespace Sharpwire.Core.Tools;

/// <summary>Outcome of a tool approval prompt (including optional session memory).</summary>
public readonly record struct ToolApprovalUiResult(bool Approved, bool AlwaysAllowForSession);
