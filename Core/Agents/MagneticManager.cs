using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Sharpwire.ViewModels;
using Sharpwire.Core.Agents;

namespace Sharpwire.Core.Agents;

/// <summary>
/// The MagneticManager is responsible for the high-level orchestration logic, 
/// specifically managing the discovery and tool-generation for wired chain entries.
/// </summary>
public class MagneticManager
{
    private readonly AgentService _agentService;

    public MagneticManager(AgentService agentService)
    {
        _agentService = agentService;
    }

    /// <summary>
    /// Generates and attaches "Chain Trigger" tools to the Orchestrator 
    /// for every agent marked as a <see cref="AgentDefinition.IsChainEntry"/>.
    /// </summary>
    public void RefreshOrchestratorTools(Agent orchestrator)
    {
        // Clear existing dynamic tools to prevent duplicates or stale references
        orchestrator.Tools.Clear();
        
        // Re-add standard tools from the agent's definition
        foreach (var toolId in orchestrator.Definition.EnabledTools)
        {
            if (_agentService.AvailableTools.TryGetValue(toolId, out var tool))
            {
                orchestrator.Tools.Add(tool);
            }
        }

        // Add the Chain Trigger tools for all discovered chain entries
        var chains = _agentService.Agents.Values
            .Where(a => a.Definition.IsChainEntry && !IsBuiltInAgent(a.Definition.Name))
            .ToList();

        foreach (var chain in chains)
        {
            var captureName = chain.Definition.Name;
            var toolName = "TriggerChain_" + captureName.Replace(" ", "_");
            var toolDesc = $"Triggers the pre-defined node chain starting at '{captureName}'. Use this macro-function for tasks matching: {chain.Definition.Description}. You must provide 'instructions'.";
            
            Func<string, Task<string>> chainDelegate = async (string instructions) =>
            {
                _agentService.Log($"Orchestrator (MagneticManager): Routing to chain '{captureName}' with instructions: {instructions}");
                _agentService.RaiseAgentActivity("Orchestrator", AgentActivityKind.ToolUse);
                try
                {
                    return await Task.Run(async () =>
                            await _agentService.ExecuteAgentAsync(captureName, instructions, CancellationToken.None)
                                .ConfigureAwait(false),
                        CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _agentService.RaiseAgentActivity("Orchestrator", AgentActivityKind.Thinking);
                }
            };
            
            try
            {
                var tool = AIFunctionFactory.Create(chainDelegate, toolName, toolDesc);
                orchestrator.Tools.Add(tool);
            }
            catch (Exception ex)
            {
                _agentService.Log($"Warning: Failed to create tool for chain '{captureName}': {ex.Message}");
            }
        }
    }

    private bool IsBuiltInAgent(string name) =>
        name.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase);
}
