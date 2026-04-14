using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Sharpwire.Core.Session;

/// <summary>
/// Gate for session message history: append-only from outside; model prompts receive an isolated copy (STANDARDS immutability direction).
/// </summary>
public sealed class SessionChatTranscript
{
    private readonly object _sync = new();
    private readonly List<ChatMessage> _messages = new();

    public List<ChatMessage> CopyForModelPrompt()
    {
        lock (_sync)
            return new List<ChatMessage>(_messages);
    }

    public void AppendUser(string text)
    {
        lock (_sync)
            _messages.Add(new ChatMessage(ChatRole.User, text));
    }

    public void AppendMessages(IEnumerable<ChatMessage> messages)
    {
        lock (_sync)
            _messages.AddRange(messages);
    }

    public void Clear()
    {
        lock (_sync)
            _messages.Clear();
    }
}
