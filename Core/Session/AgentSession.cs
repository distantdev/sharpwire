namespace Sharpwire.Core.Session;

/// <summary>
/// Per-workspace session facade: authoritative <see cref="IStateStore"/> plus runtime chat transcript.
/// Future: MAF AgentSession, pending approvals, YAML-backed definitions.
/// </summary>
public sealed class AgentSession
{
    public IStateStore State { get; }
    public SessionChatTranscript Transcript { get; } = new();

    public AgentSession(IStateStore state)
    {
        State = state;
    }

    public PendingApprovalSnapshot? GetPendingApproval() => State.LoadPendingApproval();

    public void SetPendingApproval(PendingApprovalSnapshot? snapshot) => State.SavePendingApproval(snapshot);
}
