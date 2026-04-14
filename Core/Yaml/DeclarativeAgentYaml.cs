using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sharpwire.Core.Agents;
using Sharpwire.Core.Session;
using Sharpwire.Core.Workflow;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Sharpwire.Core.Yaml;

/// <summary>
/// Loads STANDARDS-oriented declarative agent YAML (<c>kind: Agent</c>, <c>metadata</c>, <c>spec</c>) from <c>.sharpwire/agents/*.yaml</c>.
/// </summary>
public static class DeclarativeAgentYaml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<AgentDefinition> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<AgentDefinition>();

        var list = new List<AgentDefinition>();
        foreach (var path in Directory.GetFiles(directory, "*.yaml").Concat(Directory.GetFiles(directory, "*.yml")))
        {
            try
            {
                var text = File.ReadAllText(path);
                var doc = Deserializer.Deserialize<YamlAgentDocument>(text);
                if (doc == null)
                    continue;
                var def = doc.ToAgentDefinition();
                if (def != null)
                    list.Add(def);
            }
            catch
            {
                /* skip invalid file */
            }
        }

        return list;
    }

    /// <summary>Merges <c>spec.return_logic</c> from YAML agents into persisted <see cref="IStateStore"/> workflow edges (Return kind).</summary>
    public static void MergeReturnWorkflowEdgesFromDirectory(string directory, IStateStore store, IEnumerable<string> knownAgentNames)
    {
        if (!Directory.Exists(directory))
            return;

        var names = new HashSet<string>(knownAgentNames, StringComparer.OrdinalIgnoreCase);
        var extras = new List<WorkflowEdgeRecord>();
        foreach (var path in Directory.GetFiles(directory, "*.yaml").Concat(Directory.GetFiles(directory, "*.yml")))
        {
            try
            {
                var text = File.ReadAllText(path);
                var doc = Deserializer.Deserialize<YamlAgentDocument>(text);
                var edge = doc?.ToReturnEdge(names);
                if (edge != null)
                    extras.Add(edge);
            }
            catch
            {
                /* skip */
            }
        }

        if (extras.Count == 0)
            return;

        var list = store.LoadWorkflowEdges().ToList();
        foreach (var e in extras)
        {
            list.RemoveAll(x =>
                string.Equals(x.From, e.From, StringComparison.OrdinalIgnoreCase)
                && x.Kind == WorkflowEdgeKind.Return);
            list.Add(e);
        }

        store.SaveWorkflowEdges(list);
    }

    private sealed class YamlAgentDocument
    {
        public string? Kind { get; init; }
        public YamlMetadata? Metadata { get; init; }
        public YamlSpec? Spec { get; init; }

        public AgentDefinition? ToAgentDefinition()
        {
            if (Metadata == null || string.IsNullOrWhiteSpace(Metadata.Name))
                return null;
            if (!string.Equals(Kind, "Agent", StringComparison.OrdinalIgnoreCase))
                return null;

            var spec = Spec ?? new YamlSpec();
            return new AgentDefinition
            {
                Name = Metadata.Name.Trim(),
                Role = spec.Role ?? string.Empty,
                Description = spec.Description ?? string.Empty,
                Instructions = spec.Instructions ?? string.Empty,
                NextAgentName = string.IsNullOrWhiteSpace(spec.NextAgentName) ? "Orchestrator" : spec.NextAgentName.Trim(),
                EnabledTools = spec.Tools?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList() ?? new List<string>(),
                AccentColor = string.IsNullOrWhiteSpace(spec.AccentColor) ? "#0D7377" : spec.AccentColor.Trim(),
                IsChainEntry = Metadata.IsChainEntry ?? false,
                JsonSchema = spec.JsonSchema,
                LlmProvider = spec.Provider?.Trim(),
                ModelId = spec.Model?.Trim()
            };
        }

        public WorkflowEdgeRecord? ToReturnEdge(HashSet<string> knownAgents)
        {
            if (Metadata == null || string.IsNullOrWhiteSpace(Metadata.Name))
                return null;
            if (!string.Equals(Kind, "Agent", StringComparison.OrdinalIgnoreCase))
                return null;
            var rl = Spec?.ReturnLogic;
            if (rl == null || string.IsNullOrWhiteSpace(rl.Target))
                return null;
            var from = Metadata.Name.Trim();
            var target = rl.Target.Trim();
            if (!knownAgents.Contains(target))
                return null;

            return new WorkflowEdgeRecord
            {
                From = from,
                To = target,
                Kind = WorkflowEdgeKind.Return,
                ConditionRef = "review_failed",
                Label = string.IsNullOrWhiteSpace(rl.Condition) ? "YAML return" : rl.Condition.Trim()
            };
        }
    }

    private sealed class YamlMetadata
    {
        public string? Name { get; init; }
        public bool? IsChainEntry { get; init; }
    }

    private sealed class YamlSpec
    {
        public string? Provider { get; init; }
        public string? Model { get; init; }
        public string? Role { get; init; }
        public string? Description { get; init; }
        public string? Instructions { get; init; }
        public string? NextAgentName { get; init; }
        public List<string>? Tools { get; init; }
        public string? AccentColor { get; init; }
        public string? JsonSchema { get; init; }
        public YamlReturnLogic? ReturnLogic { get; init; }
    }

    private sealed class YamlReturnLogic
    {
        public string? Condition { get; init; }
        public string? Target { get; init; }
    }
}
