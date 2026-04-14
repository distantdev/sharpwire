using System.Collections.Generic;
using Sharpwire.Core.Agents;
using Sharpwire.Core.Workflow;

namespace Sharpwire.Core.Session;

/// <summary>
/// Authoritative persistence for user-defined agents and graph layout (STANDARDS: source of truth outside view models).
/// </summary>
public interface IStateStore
{
    IReadOnlyList<AgentDefinition> LoadCustomAgentDefinitions();

    void SaveCustomAgentDefinitions(IReadOnlyList<AgentDefinition> definitions);

    bool TryGetNodeLayout(string agentName, out NodePosition position);

    void SetNodeLayout(string agentName, in NodePosition position);

    void RemoveNodeLayout(string agentName);

    void RenameNodeLayout(string oldName, string newName);

    PendingApprovalSnapshot? LoadPendingApproval();

    void SavePendingApproval(PendingApprovalSnapshot? snapshot);

    /// <summary>Built-in agent handoff targets (Coder/Reviewer/Orchestrator); null value clears override.</summary>
    IReadOnlyDictionary<string, string> LoadHandoffOverrides();

    void SetHandoffOverride(string fromAgent, string? toAgent);

    void RemoveHandoffOverride(string fromAgent);

    void RenameHandoffOverrideKey(string oldFromName, string newFromName);

    void RenameHandoffTarget(string oldTargetName, string newTargetName);

    void RemoveHandoffReferencesToAgent(string removedAgentName);

    IReadOnlyList<WorkflowEdgeRecord> LoadWorkflowEdges();

    void SaveWorkflowEdges(IReadOnlyList<WorkflowEdgeRecord> edges);

    WorkflowRunStateDto LoadWorkflowRunState();

    void SaveWorkflowRunState(WorkflowRunStateDto state);

    /// <summary>Deletes all session data (agents, layout, handoffs) in the current workspace.</summary>
    void ClearAllData();

    /// <summary>Saves a named scene (agents, layout, handoffs) into a sub-directory.</summary>
    void SaveScene(string sceneName);

    /// <summary>Loads a named scene by copying its files back to the root session directory.</summary>
    void LoadScene(string sceneName);

    /// <summary>Returns a list of saved scene names.</summary>
    IReadOnlyList<string> GetSceneNames();

    /// <summary>Forces a reload of cached layout and handoff data from disk.</summary>
    void Reload();
}
