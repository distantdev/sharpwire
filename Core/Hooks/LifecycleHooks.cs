using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sharpwire.Core.Hooks;

public enum LifecycleHookStage
{
    OrchestratorTurnStart,
    OrchestratorTurnEnd,
    AgentExecutionStart,
    AgentExecutionEnd,
    AgentStepStart,
    AgentStepEnd,
    ChainHandoff,
    ToolApprovalRequest,
    ToolApprovalResult
}

public abstract class LifecycleHookContext
{
    protected LifecycleHookContext(LifecycleHookStage stage)
    {
        Stage = stage;
    }

    public LifecycleHookStage Stage { get; }
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
    public bool IsBlocked { get; private set; }
    public string? BlockReason { get; private set; }

    public void Block(string reason)
    {
        IsBlocked = true;
        BlockReason = string.IsNullOrWhiteSpace(reason) ? "Blocked by lifecycle hook." : reason;
    }
}

public sealed class OrchestratorTurnHookContext : LifecycleHookContext
{
    public OrchestratorTurnHookContext(LifecycleHookStage stage, string task) : base(stage)
    {
        Task = task;
    }

    public string Task { get; set; }
    public string? ResponseText { get; set; }
    public bool ResponseUsedTools { get; set; }
}

public sealed class AgentExecutionHookContext : LifecycleHookContext
{
    public AgentExecutionHookContext(LifecycleHookStage stage, string agentName, string task) : base(stage)
    {
        AgentName = agentName;
        Task = task;
    }

    public string AgentName { get; }
    public string Task { get; set; }
    public string? ResponseText { get; set; }
    public bool ResponseUsedTools { get; set; }
}

public sealed class AgentStepHookContext : LifecycleHookContext
{
    public AgentStepHookContext(LifecycleHookStage stage, string agentName, string task) : base(stage)
    {
        AgentName = agentName;
        Task = task;
    }

    public string AgentName { get; }
    public string Task { get; set; }
    public string? ResponseText { get; set; }
    public bool ResponseUsedTools { get; set; }
}

public sealed class ChainHandoffHookContext : LifecycleHookContext
{
    public ChainHandoffHookContext(string fromAgent, string toAgent, string handoffTask, bool isReturnEdge)
        : base(LifecycleHookStage.ChainHandoff)
    {
        FromAgent = fromAgent;
        ToAgent = toAgent;
        HandoffTask = handoffTask;
        IsReturnEdge = isReturnEdge;
    }

    public string FromAgent { get; }
    public string ToAgent { get; set; }
    public string HandoffTask { get; set; }
    public bool IsReturnEdge { get; }
}

public sealed class ToolApprovalHookContext : LifecycleHookContext
{
    public ToolApprovalHookContext(LifecycleHookStage stage, string toolId, string details) : base(stage)
    {
        ToolId = toolId;
        Details = details;
    }

    public string ToolId { get; }
    public string Details { get; set; }
    public bool Approved { get; set; }
}

public delegate Task LifecycleHookNext(CancellationToken ct);

public interface ILifecycleHookMiddleware
{
    int Order => 0;
    Task InvokeAsync(LifecycleHookContext context, LifecycleHookNext next, CancellationToken ct);
}

public sealed class LifecycleHookPipeline
{
    private readonly List<ILifecycleHookMiddleware> _hooks;

    public LifecycleHookPipeline(IEnumerable<ILifecycleHookMiddleware> hooks)
    {
        _hooks = hooks
            .OrderBy(h => h.Order)
            .ThenBy(h => h.GetType().FullName, StringComparer.Ordinal)
            .ToList();
    }

    public static LifecycleHookPipeline Empty { get; } = new(Array.Empty<ILifecycleHookMiddleware>());

    public bool HasHooks => _hooks.Count > 0;

    public async Task ExecuteAsync(LifecycleHookContext context, CancellationToken ct)
    {
        var index = -1;

        Task Next(CancellationToken token)
        {
            if (context.IsBlocked)
                return Task.CompletedTask;

            index++;
            if (index >= _hooks.Count)
                return Task.CompletedTask;

            return _hooks[index].InvokeAsync(context, Next, token);
        }

        await Next(ct).ConfigureAwait(false);
    }
}
