namespace Sharpwire.ViewModels;

/// <summary>Read-only view of an agent's accumulated streamed output for the app session.</summary>
public sealed class AgentLogViewModel
{
    public AgentLogViewModel(string agentKey, AgentModelStreamMonitor monitor)
    {
        AgentKey = agentKey;
        Monitor = monitor;
    }

    public string AgentKey { get; }

    public AgentModelStreamMonitor Monitor { get; }
}
