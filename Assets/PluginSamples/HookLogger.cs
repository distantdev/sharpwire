using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Sharpwire.Core.Hooks;

[Description("Sample lifecycle hook middleware that reports each hook event to chat.")]
public sealed class HookLogger : ILifecycleHookMiddleware
{
    public int Order => 100;

    public async Task InvokeAsync(LifecycleHookContext context, LifecycleHookNext next, CancellationToken ct)
    {
        var message = $"[{nameof(HookLogger)}] {context.Stage} ({context.GetType().Name})";

        switch (context)
        {
            case OrchestratorTurnHookContext o:
                message += $" TaskLen={o.Task?.Length ?? 0}";
                break;
            case AgentExecutionHookContext e:
                message += $" Agent={e.AgentName}";
                break;
            case AgentStepHookContext s:
                message += $" Agent={s.AgentName}";
                break;
            case ChainHandoffHookContext h:
                message += $" {h.FromAgent} -> {h.ToAgent} Return={h.IsReturnEdge}";
                break;
            case ToolApprovalHookContext t:
                message += $" Tool={t.ToolId} Approved={t.Approved}";
                break;
        }

        PluginChat.TryPostSystem(message, nameof(HookLogger));
        await next(ct).ConfigureAwait(false);
    }
}
