using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sharpwire.Core.Agents;

public partial class AgentService
{
    public void SaveScene(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        
        SendLogLine($"System: Saving scene '{name}'...");
        SaveAgentDefinitions(); // Flushes Agents dictionary to agents.json
        // Layout and Handoffs are flushed automatically on change in FileStateStore, 
        // but we can't be too sure if there are pending UI changes.
        _session.State.SaveScene(name);
        SendLogLine($"System: Scene '{name}' saved.");
    }

    public async Task LoadSceneAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        SendLogLine($"System: Loading scene '{name}'...");
        _session.State.LoadScene(name);
        _session.State.Reload();
        
        // Re-initialize agents from the newly copied files
        await InitializeAgentsAsync(ct).ConfigureAwait(false);
        
        OnSceneLoaded?.Invoke();
        SendLogLine($"System: Scene '{name}' loaded.");
    }

    public IReadOnlyList<string> GetSceneNames()
    {
        return _session.State.GetSceneNames();
    }

    public async Task ClearSceneAsync(CancellationToken ct)
    {
        SendLogLine("System: Clearing current scene...");
        _session.State.ClearAllData();
        _session.State.Reload();
        _session.Transcript.Clear();
        
        // Re-initialize to get default agents (Orchestrator, Coder, Reviewer)
        await InitializeAgentsAsync(ct).ConfigureAwait(false);
        
        OnSceneLoaded?.Invoke();
        SendLogLine("System: Scene cleared.");
    }
}
