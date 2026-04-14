using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Sharpwire.Core;
using Sharpwire.Core.Session;
using Sharpwire.Core.Workflow;
using Sharpwire.Core.Hooks;
using Sharpwire.ViewModels;

namespace Sharpwire.Core.Agents;

public partial class AgentService
{
    private const string MafEndExecutorId = "__end";
    private const string MafHitlExecutorId = "__hitl";

    private bool _mafHitlLatch;

    private static string LoopIterationKey(string fromAgent, string returnTarget) =>
        $"{fromAgent}_return_{returnTarget}";

    private void ResetLoopIterationsForAgent(string fromAgent)
    {
        var rs = _session.State.LoadWorkflowRunState();
        var keys = rs.LoopIterations.Keys
            .Where(k => k.StartsWith($"{fromAgent}_return_", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var k in keys)
            rs.LoopIterations.Remove(k);
        rs.PausedForHitl = false;
        rs.HitlSummary = null;
        _session.State.SaveWorkflowRunState(rs);
    }

    private (bool Tripwire, WorkflowReviewResult? Review) EvaluateTripwireForReturns(
        string agentName,
        WorkflowReviewResult? review,
        IReadOnlyList<WorkflowEdgeRecord> allEdges,
        int maxLoopIterations)
    {
        if (review?.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) != true)
            return (false, review);

        var returnTargets = allEdges
            .Where(e => string.Equals(e.From, agentName, StringComparison.OrdinalIgnoreCase)
                        && e.Kind == WorkflowEdgeKind.Return)
            .Select(e => e.To)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (returnTargets.Count == 0)
            return (false, review);
        if (maxLoopIterations <= 0)
            return (false, review);

        var runState = _session.State.LoadWorkflowRunState();
        var tripwire = false;
        foreach (var rt in returnTargets)
        {
            var key = LoopIterationKey(agentName, rt);
            var c = runState.LoopIterations.GetValueOrDefault(key);
            if (c >= maxLoopIterations)
            {
                tripwire = true;
                break;
            }
        }

        if (!tripwire)
        {
            foreach (var rt in returnTargets)
            {
                var key = LoopIterationKey(agentName, rt);
                runState.LoopIterations[key] = runState.LoopIterations.GetValueOrDefault(key) + 1;
            }
        }

        runState.PausedForHitl = tripwire;
        runState.HitlSummary = tripwire ? review.Critique : null;
        _session.State.SaveWorkflowRunState(runState);
        return (tripwire, review);
    }

    private async Task<string?> TryExecuteHandoffWorkflowAsync(string entryAgent, string task, CancellationToken ct)
    {
        _mafHitlLatch = false;
        var edges = GetEffectiveWorkflowEdges().ToList();
        this.Log($"[MAF] Starting workflow for '{entryAgent}' with {edges.Count} edges.");

        if (edges.Count == 0)
        {
            this.Log("[MAF] No edges found; falling back to linear.");
            return null;
        }

        if (!Agents.ContainsKey(entryAgent))
        {
            this.Log($"[MAF] Entry agent '{entryAgent}' not found in registry.");
            return null;
        }

        var validAgents = new HashSet<string>(Agents.Keys, StringComparer.OrdinalIgnoreCase);
        var reachable = CollectReachableAgents(entryAgent, edges, validAgents);
        this.Log($"[MAF] Reachable agents: {string.Join(", ", reachable)}");

        if (!reachable.Contains(entryAgent))
        {
            this.Log($"[MAF] Entry agent '{entryAgent}' not in reachable set (unexpected).");
            return null;
        }

        var maxLoop = Math.Max(0, _settingsManager.Load(_apiKeyStore).MaxLoopIterations);
        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.OrdinalIgnoreCase);
        var opts = ExecutorOptions.Default;

        foreach (var id in reachable)
        {
            var captureId = id;
            bindings[id] = ExecutorBindingExtensions.BindAsExecutor<HandoffPayload, HandoffPayload>(
                async (p, ctx, c) => await RunAgentExecutorStepAsync(captureId, p, ctx, c, edges, maxLoop).ConfigureAwait(false),
                id,
                opts,
                false);
        }

        bindings[MafEndExecutorId] = ExecutorBindingExtensions.BindAsExecutor<HandoffPayload, HandoffPayload>(
            (p, ctx, c) =>
            {
                this.Log("[MAF] __end executor reached.");
                return new ValueTask<HandoffPayload>(p);
            },
            MafEndExecutorId,
            opts,
            false);

        var needsHitl = reachable.Any(agentId => edges.Any(x => string.Equals(x.From, agentId, StringComparison.OrdinalIgnoreCase) && x.Kind == WorkflowEdgeKind.Return));
        if (needsHitl)
        {
            bindings[MafHitlExecutorId] = ExecutorBindingExtensions.BindAsExecutor<HandoffPayload, HandoffPayload>(
                (p, ctx, c) =>
                {
                    var summary = string.IsNullOrWhiteSpace(p.Review?.Critique)
                        ? "Workflow paused: review loop limit reached."
                        : p.Review!.Critique!;
                    _mafHitlLatch = true;
                    EmitChatMessage(MessageRole.System,
                        $"HITL: Review loop limit reached. {summary}",
                        "System",
                        null);
                    _session.SetPendingApproval(new PendingApprovalSnapshot(
                        "WorkflowHITL",
                        summary,
                        null,
                        DateTime.UtcNow.ToString("O")));
                    
                    this.Log("[MAF] __hitl executor reached.");
                    return new ValueTask<HandoffPayload>(p);
                },
                MafHitlExecutorId,
                opts,
                false);
        }

        if (!bindings.TryGetValue(entryAgent, out var startBinding))
        {
            this.Log($"[MAF] FAILED to get binding for entry agent '{entryAgent}'.");
            return null;
        }

        var builder = new WorkflowBuilder(startBinding)
            .WithName("SharpwireHandoffGraph")
            .WithOutputFrom(bindings.Values.ToArray());

        static string ResolveTargetExecutorId(string to) =>
            to.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase) ? MafEndExecutorId : to;

        this.Log($"[MAF] Building workflow with {edges.Count} candidate edges.");

        bool EvaluateCondition(HandoffPayload? p, string? conditionRef, bool defaultIfEmpty, string agentName)
        {
            if (string.IsNullOrWhiteSpace(conditionRef))
                return defaultIfEmpty;

            if (p == null)
            {
                this.Log($"[MAF] EvaluateCondition ({agentName}): Payload is null, returning false.");
                return false;
            }

            // Built-in MAF logic: review_failed / review_passed
            if (conditionRef.Equals("review_failed", StringComparison.OrdinalIgnoreCase))
            {
                var failed = p.Review?.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true;
                this.Log($"[MAF] EvaluateCondition ({agentName}): review_failed check -> {failed}");
                return failed;
            }
            if (conditionRef.Equals("review_passed", StringComparison.OrdinalIgnoreCase))
            {
                var passed = p.Review == null || p.Review.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase) == true;
                this.Log($"[MAF] EvaluateCondition ({agentName}): review_passed check -> {passed}");
                return passed;
            }

            // Generic JSON predicate: "property == value"
            if (!string.IsNullOrWhiteSpace(p.JsonOutput) && conditionRef.Contains("=="))
            {
                try
                {
                    var parts = conditionRef.Split("==", 2);
                    var key = parts[0].Trim();
                    var val = parts[1].Trim().Trim('\'', '\"');

                    using var doc = System.Text.Json.JsonDocument.Parse(p.JsonOutput);
                    if (doc.RootElement.TryGetProperty(key, out var prop))
                    {
                        var actual = prop.ValueKind == System.Text.Json.JsonValueKind.String 
                            ? prop.GetString() 
                            : prop.GetRawText();
                        var matches = string.Equals(actual, val, StringComparison.OrdinalIgnoreCase);
                        this.Log($"[MAF] EvaluateCondition ({agentName}): JSON check {key} == {val} -> {matches}");
                        return matches;
                    }
                }
                catch { /* ignore invalid JSON or predicate */ }
            }

            return false;
        }

        foreach (var e in edges)
        {
            if (!reachable.Contains(e.From))
                continue;
            var targetId = ResolveTargetExecutorId(e.To);
            if (targetId != MafEndExecutorId && targetId != MafHitlExecutorId && !reachable.Contains(targetId))
                continue;
            if (!bindings.TryGetValue(e.From, out var fromB0) || !bindings.TryGetValue(targetId, out var toB0))
                continue;
            var fromB = fromB0!;
            var toB = toB0!;

            this.Log($"[MAF] Adding edge: {e.From} -> {targetId} (Kind: {e.Kind}, Cond: {e.ConditionRef})");
            var fromHasExplicitEdges = edges.Any(x =>
                string.Equals(x.From, e.From, StringComparison.OrdinalIgnoreCase)
                && (!string.IsNullOrEmpty(x.ConditionRef) || x.Kind == WorkflowEdgeKind.Return));

            var captureFrom = e.From;
            var captureTarget = targetId;
            var captureCond = e.ConditionRef;
            var captureLabel = e.Label;

            if (e.Kind == WorkflowEdgeKind.Return)
            {
                builder = builder.AddEdge<HandoffPayload>(
                    fromB,
                    toB,
                    p => {
                        this.Log($"[MAF] Edge predicate evaluating: {captureFrom} -> {captureTarget} (Return)");
                        var result = p is { TripwireHit: false } && EvaluateCondition(p, e.ConditionRef ?? "review_failed", true, captureFrom);
                        if (result && targetId != MafEndExecutorId && targetId != MafHitlExecutorId) OnAgentHandoffFlow?.Invoke(captureFrom, captureTarget, true);
                        this.Log($"[MAF] Result: {result}");
                        return result;
                    },
                    e.Label ?? "Retry",
                    true)!;
            }
            else if (!string.IsNullOrEmpty(e.ConditionRef))
            {
                builder = builder.AddEdge<HandoffPayload>(
                    fromB,
                    toB,
                    p => {
                        this.Log($"[MAF] Edge predicate evaluating: {captureFrom} -> {captureTarget} (Cond: {captureCond})");
                        var result = EvaluateCondition(p, captureCond, false, captureFrom);
                        if (result && targetId != MafEndExecutorId && targetId != MafHitlExecutorId) OnAgentHandoffFlow?.Invoke(captureFrom, captureTarget, false);
                        this.Log($"[MAF] Result: {result}");
                        return result;
                    },
                    e.Label ?? e.ConditionRef,
                    true)!;
            }
            else
            {
                if (fromHasExplicitEdges)
                {
                    builder = builder.AddEdge<HandoffPayload>(
                        fromB,
                        toB,
                        p => {
                            this.Log($"[MAF] Edge predicate evaluating: {captureFrom} -> {captureTarget} (Default-with-others)");
                            var result = p == null || p.Review == null || p.Review.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase);
                            if (result && targetId != MafEndExecutorId && targetId != MafHitlExecutorId) OnAgentHandoffFlow?.Invoke(captureFrom, captureTarget, false);
                            this.Log($"[MAF] Result: {result}");
                            return result;
                        },
                        e.Label ?? "Passed",
                        true)!;
                }
                else
                {
                    this.Log($"[MAF] Adding unconditional edge: {captureFrom} -> {targetId}");
                    builder = builder.AddEdge<HandoffPayload>(
                        fromB, 
                        toB, 
                        p => {
                            this.Log($"[MAF] Edge predicate evaluating: {captureFrom} -> {captureTarget} (Unconditional)");
                            if (targetId != MafEndExecutorId && targetId != MafHitlExecutorId) OnAgentHandoffFlow?.Invoke(captureFrom, captureTarget, false);
                            return true; 
                        }, 
                        e.Label ?? "Next", 
                        true)!;
                }
            }
        }

        foreach (var agentId in reachable)
        {
            if (!edges.Any(x =>
                    string.Equals(x.From, agentId, StringComparison.OrdinalIgnoreCase)
                    && x.Kind == WorkflowEdgeKind.Return))
                continue;
            if (!bindings.TryGetValue(agentId, out var fromB1) || !bindings.TryGetValue(MafHitlExecutorId, out var hitlB0))
                continue;
            var fromB = fromB1!;
            var hitlB = hitlB0!;
            this.Log($"[MAF] Adding HITL tripwire edge for {agentId}");
            var captureAgentId = agentId;
            builder = builder.AddEdge<HandoffPayload>(
                fromB,
                hitlB,
                p => {
                    this.Log($"[MAF] Edge predicate evaluating: {captureAgentId} -> {MafHitlExecutorId} (HITL Tripwire)");
                    var result = p is { TripwireHit: true } && EvaluateCondition(p, "review_failed", true, captureAgentId);
                    this.Log($"[MAF] Result: {result}");
                    return result;
                },
                "HITL (tripwire)",
                true)!;
        }

        Microsoft.Agents.AI.Workflows.Workflow workflow;
        try
        {
            workflow = builder.Build();
            this.Log("[MAF] Workflow built successfully.");
        }
        catch (Exception ex)
        {
            this.Log($"[MAF] FAILED to build workflow: {ex.Message}");
            return null;
        }

        var input = new HandoffPayload { InitialUserTask = task, CurrentTask = task };
        this.Log("[MAF] Initiating native RunStreamingAsync...");
        await using var run = await InProcessExecution.OffThread
            .RunStreamingAsync<HandoffPayload>(workflow, input, sessionId: null, ct)
            .ConfigureAwait(false);

        var allOutputs = new List<string>();

        try
        {
            await foreach (var ev in run.WatchStreamAsync().WithCancellation(ct).ConfigureAwait(false))
            {
                if (_mafHitlLatch)
                {
                    this.Log("[MAF] Workflow paused for HITL. Canceling stream...");
                    break;
                }

                if (ev is WorkflowOutputEvent wo)
                {
                    try
                    {
                        if (wo.IsType(typeof(HandoffPayload)))
                        {
                            var outputPayload = wo.As<HandoffPayload>();
                            var text = ChatTextNormalizer.ForDisplay(outputPayload?.LastAssistantText);
                            if (!string.IsNullOrWhiteSpace(text) && !allOutputs.Contains(text))
                            {
                                allOutputs.Add(text);
                            }
                        }
                    }
                    catch { /* ignore */ }
                }
                else if (ev is WorkflowErrorEvent err)
                {
                    this.Log($"[MAF] Workflow Error: {err.Exception?.ToString()}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            this.Log("[MAF] Workflow stream canceled.");
        }

        this.Log("[MAF] Run finished naturally.");

        if (_mafHitlLatch)
            return _session.State.LoadWorkflowRunState().HitlSummary ?? "Workflow paused for human review (loop limit).";

        if (allOutputs.Count == 0)
            return string.Empty;

        return allOutputs.Count == 1 
            ? allOutputs[0] 
            : string.Join("\n\n---\n\n", allOutputs);
    }

    private async Task<HandoffPayload> RunAgentExecutorStepAsync(
        string agentName,
        HandoffPayload? payload,
        IWorkflowContext ctx,
        CancellationToken ct,
        IReadOnlyList<WorkflowEdgeRecord> edges,
        int maxLoopIterations)
    {
        if (payload == null)
        {
            this.Log($"[MAF] Agent '{agentName}' received null payload.");
            return new HandoffPayload();
        }

        if (!Agents.TryGetValue(agentName, out var activeAgent))
        {
            this.Log($"[MAF] Agent '{agentName}' not found in step execution.");
            return payload;
        }

        var stepStartContext = new AgentStepHookContext(LifecycleHookStage.AgentStepStart, agentName, payload.CurrentTask);
        await RunLifecycleHooksAsync(stepStartContext, ct).ConfigureAwait(false);
        if (stepStartContext.IsBlocked)
        {
            this.Log($"[MAF] Step blocked for {agentName}: {stepStartContext.BlockReason}");
            return payload.WithRunResult(agentName, stepStartContext.BlockReason ?? "Blocked by hook.", null, payload.Review, payload.TripwireHit);
        }

        this.Log($"[MAF] Executing step for agent: {agentName}");
        OnAgentActivityChanged?.Invoke(agentName, AgentActivityKind.Thinking);
        BeginModelStreamBatch(agentName);
        OnAgentModelStreamStarted?.Invoke(agentName, AccentForSender(agentName));
        ChatResponse response;
        var hasReturnEdges = edges.Any(e => string.Equals(e.From, agentName, StringComparison.OrdinalIgnoreCase) && e.Kind == WorkflowEdgeKind.Return);
        try
        {
            var originalAsk = string.IsNullOrWhiteSpace(payload.InitialUserTask)
                ? payload.CurrentTask
                : payload.InitialUserTask;

            string currentTask;
            if (string.IsNullOrEmpty(payload.LastAgent))
            {
                currentTask = stepStartContext.Task;
            }
            else if (payload.Review?.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true)
            {
                currentTask = $"Original task:\n{originalAsk}\n\n{payload.LastAgent} REJECTED the previous work with this critique:\n{payload.Review.Critique ?? payload.LastAssistantText}\n\nPlease fix the issues and try again.";
            }
            else
            {
                currentTask = $"Original task:\n{originalAsk}\n\n{payload.LastAgent} finished with this result: {(string.IsNullOrWhiteSpace(payload.LastAssistantText) ? "(no assistant text in the last reply)" : payload.LastAssistantText)}\n\nPlease continue the work.";
            }

            response = await activeAgent.GetResponseAsync(
                    currentTask,
                    _session.Transcript.CopyForModelPrompt(),
                    ct,
                    d => AppendModelStreamBatch(agentName, d),
                    hasReturnEdges)
                .ConfigureAwait(false);
        }
        finally
        {
            FlushModelStreamBatch(agentName);
            OnAgentModelStreamEnded?.Invoke(agentName);
        }

        _session.Transcript.AppendMessages(response.Messages);

        var lastResponse = LastNonEmptyAssistantText(response);
        var stepEndContext = new AgentStepHookContext(LifecycleHookStage.AgentStepEnd, agentName, stepStartContext.Task)
        {
            ResponseText = lastResponse,
            ResponseUsedTools = ResponseUsedTools(response),
            CorrelationId = stepStartContext.CorrelationId
        };
        await RunLifecycleHooksAsync(stepEndContext, ct).ConfigureAwait(false);
        lastResponse = stepEndContext.ResponseText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(lastResponse))
            EmitChatMessage(MessageRole.Agent, lastResponse, agentName, AccentForSender(agentName));

        if (stepEndContext.ResponseUsedTools)
        {
            OnAgentActivityChanged?.Invoke(agentName, AgentActivityKind.ToolUse);
            try
            {
                await Task.Delay(320, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        OnAgentActivityChanged?.Invoke(agentName, AgentActivityKind.Idle);
        OnAgentStateChanged?.Invoke(agentName, AgentState.Idle);

        var assistantText = LastNonEmptyAssistantText(response);
        var jsonOutput = !string.IsNullOrWhiteSpace(activeAgent.Definition.JsonSchema) ? assistantText : null;

        var review = WorkflowReviewParser.TryParseReview(agentName, assistantText, hasReturnEdges, response);
        if (review?.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase) == true)
            ResetLoopIterationsForAgent(agentName);

        var (tripwire, _) = EvaluateTripwireForReturns(agentName, review, edges, maxLoopIterations);
        var nextPayload = payload.WithRunResult(agentName, assistantText, jsonOutput, review, tripwire);

        this.Log($"[MAF] Step completed for {agentName}. Returning next payload.");
        return nextPayload;
    }

    private static HashSet<string> CollectReachableAgents(
        string start,
        List<WorkflowEdgeRecord> edges,
        HashSet<string> validAgents)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!validAgents.Contains(start))
            return visited;
        var q = new Queue<string>();
        q.Enqueue(start);
        visited.Add(start);
        while (q.Count > 0)
        {
            var a = q.Dequeue();
            foreach (var e in edges)
            {
                if (!string.Equals(e.From, a, StringComparison.OrdinalIgnoreCase))
                    continue;
                var to = e.To;
                if (to.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!validAgents.Contains(to))
                    continue;
                if (visited.Add(to))
                    q.Enqueue(to);
            }
        }

        return visited;
    }
}