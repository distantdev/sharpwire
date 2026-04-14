using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SharpwirePlugins.Memory
{
    /// <summary>
    /// Memory Plugin: Provides an agent with both short-term (key-value) and long-term (semantic) memory capabilities.
    /// </summary>
    public sealed class MemoryPlugin
    {
        private const string MemoryFilePath = "agent_memory.json";
        private const string StateFilePath = "agent_state.json";

        private List<MemoryEntry> _memories = new List<MemoryEntry>();
        private Dictionary<string, string> _state = new Dictionary<string, string>();

        public MemoryPlugin()
        {
            LoadFromDisk();
        }

        private void LoadFromDisk()
        {
            try
            {
                if (File.Exists(MemoryFilePath))
                {
                    var json = File.ReadAllText(MemoryFilePath);
                    _memories = JsonSerializer.Deserialize<List<MemoryEntry>>(json) ?? new List<MemoryEntry>();
                }

                if (File.Exists(StateFilePath))
                {
                    var json = File.ReadAllText(StateFilePath);
                    _state = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MemoryPlugin] Error loading memory: {ex.Message}");
            }
        }

        private void SaveToDisk()
        {
            try
            {
                var memoryJson = JsonSerializer.Serialize(_memories, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(MemoryFilePath, memoryJson);

                var stateJson = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StateFilePath, stateJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MemoryPlugin] Error saving memory: {ex.Message}");
            }
        }

        [Description("Stores an important fact, experience, or note for long-term recall. Useful for maintaining continuity across sessions.")]
        public string Store(
            [Description("The actual content of the memory to save.")] string content,
            [Description("Comma-separated tags (e.g., 'user_preference', 'task_history', 'context').")] string tags = "")
        {
            var entry = new MemoryEntry
            {
                Content = content,
                Tags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList(),
                Timestamp = DateTime.UtcNow
            };

            _memories.Add(entry);
            SaveToDisk();

            return $"Memory stored successfully with {entry.Tags.Count} tags. (ID: {entry.Timestamp.Ticks})";
        }

        [Description("Recalls relevant memories or facts based on a semantic search query. This helps the agent 'remember' past context.")]
        public string Recall(
            [Description("The query or keyword to search for in past memories.")] string query,
            [Description("The maximum number of relevant memories to retrieve.")] int limit = 3)
        {
            if (!_memories.Any())
            {
                return "I don't have any memories stored yet.";
            }

            // Simple semantic retrieval mock (keyword/similarity-based)
            // In a real implementation, this would use vector embeddings and a database like Pinecone or Qdrant.
            var results = _memories
                .Select(m => new { Memory = m, Score = CalculateRelevance(m, query) })
                .Where(r => r.Score > 0)
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.Memory.Timestamp)
                .Take(limit)
                .ToList();

            if (!results.Any())
            {
                return $"I couldn't find any relevant memories for: '{query}'.";
            }

            var output = "RECALLED MEMORIES:\n";
            foreach (var r in results)
            {
                output += $"[{r.Memory.Timestamp:yyyy-MM-dd HH:mm}] (Tags: {string.Join(", ", r.Memory.Tags)}) - {r.Memory.Content}\n";
            }

            return output;
        }

        [Description("Sets a persistent key-value state variable. Useful for short-term flags, preferences, or atomic states.")]
        public string SetState(
            [Description("The key to identify the state variable.")] string key,
            [Description("The value to store (string).")] string value)
        {
            _state[key] = value;
            SaveToDisk();
            return $"State set: {key} = {value}";
        }

        [Description("Retrieves a persistent key-value state variable.")]
        public string GetState(
            [Description("The key of the state variable to retrieve.")] string key)
        {
            if (_state.TryGetValue(key, out var value))
            {
                return value;
            }
            return $"State variable '{key}' not found.";
        }

        [Description("Deletes all stored memories and state. USE WITH CAUTION.")]
        public string WipeMemory()
        {
            _memories.Clear();
            _state.Clear();
            SaveToDisk();
            return "Memory and state wiped successfully.";
        }

        private double CalculateRelevance(MemoryEntry m, string query)
        {
            // Simplified overlap-based scoring for the mock
            var queryTerms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var content = m.Content.ToLowerInvariant();
            var tags = string.Join(" ", m.Tags).ToLowerInvariant();

            double score = 0;
            foreach (var term in queryTerms)
            {
                if (content.Contains(term)) score += 1.0;
                if (tags.Contains(term)) score += 2.0; // Tags have higher weight
            }

            return score;
        }

        public class MemoryEntry
        {
            public string Content { get; set; } = string.Empty;
            public List<string> Tags { get; set; } = new List<string>();
            public DateTime Timestamp { get; set; }
        }
    }
}
