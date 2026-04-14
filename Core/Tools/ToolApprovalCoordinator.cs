using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sharpwire.Core.Hooks;

namespace Sharpwire.Core.Tools;

/// <summary>STANDARDS §5 — high-impact tool approval; UI registers <see cref="TryRequestApprovalAsync"/>.</summary>
public sealed class ToolApprovalCoordinator
{
    private readonly HashSet<string> _alwaysAllowForSession = new(StringComparer.OrdinalIgnoreCase);
    private LifecycleHookPipeline _lifecycleHookPipeline = LifecycleHookPipeline.Empty;

    /// <summary>Per-process session: tools added here skip the prompt until app exit.</summary>
    public void ClearSessionApprovals() => _alwaysAllowForSession.Clear();

    public Func<string, string, Task<ToolApprovalUiResult>>? TryRequestApprovalAsync { get; set; }
    public Action<string>? LogHookWarning { get; set; }

    public void SetLifecycleHookPipeline(LifecycleHookPipeline pipeline)
    {
        _lifecycleHookPipeline = pipeline ?? LifecycleHookPipeline.Empty;
    }

    private async Task RunHookContextAsync(LifecycleHookContext context)
    {
        if (!_lifecycleHookPipeline.HasHooks)
            return;

        try
        {
            await _lifecycleHookPipeline.ExecuteAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogHookWarning?.Invoke($"Hook warning ({context.Stage}): {ex.Message}");
        }
    }

    /// <summary>Used by plugins: honors session allow-list, then invokes UI if needed.</summary>
    public async Task<bool> EnsureApprovedAsync(string toolId, string details)
    {
        var preContext = new ToolApprovalHookContext(LifecycleHookStage.ToolApprovalRequest, toolId, details);
        await RunHookContextAsync(preContext).ConfigureAwait(false);
        if (preContext.IsBlocked)
            return false;

        details = preContext.Details;

        if (_alwaysAllowForSession.Contains(toolId))
        {
            var sessionAllowContext = new ToolApprovalHookContext(LifecycleHookStage.ToolApprovalResult, toolId, details)
            {
                Approved = true,
                CorrelationId = preContext.CorrelationId
            };
            await RunHookContextAsync(sessionAllowContext).ConfigureAwait(false);
            if (sessionAllowContext.IsBlocked)
                return false;
            return true;
        }

        var handler = TryRequestApprovalAsync;
        if (handler == null)
            return false;

        var r = await handler(toolId, details).ConfigureAwait(false);
        var postContext = new ToolApprovalHookContext(LifecycleHookStage.ToolApprovalResult, toolId, details)
        {
            Approved = r.Approved,
            CorrelationId = preContext.CorrelationId
        };
        await RunHookContextAsync(postContext).ConfigureAwait(false);
        if (postContext.IsBlocked)
            return false;

        var approved = postContext.Approved;
        if (approved && r.AlwaysAllowForSession)
            _alwaysAllowForSession.Add(toolId);
        return approved;
    }
}
