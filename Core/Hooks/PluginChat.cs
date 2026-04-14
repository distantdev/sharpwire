using System;
using System.Threading;

namespace Sharpwire.Core.Hooks;

/// <summary>
/// Controlled chat bridge for plugin code.
/// Plugins can request a system message via <see cref="TryPostSystem"/>,
/// while the host controls actual emission and guardrails.
/// </summary>
public static class PluginChat
{
    private const int MaxMessagesPerWindow = 6;
    private const int WindowSeconds = 30;
    private const int MaxMessageChars = 1200;

    private static readonly object Sync = new();
    private static DateTime _windowStartUtc = DateTime.MinValue;
    private static int _sentInWindow;

    private static Func<string, string?, bool>? _emitSystem;

    internal static void SetSystemEmitter(Func<string, string?, bool> emitter)
    {
        _emitSystem = emitter;
    }

    internal static void ClearSystemEmitter()
    {
        _emitSystem = null;
    }

    /// <summary>
    /// Attempts to post a plugin-originated system message to chat.
    /// Returns false when blocked by guardrails or when host is unavailable.
    /// </summary>
    public static bool TryPostSystem(string text, string? source = null)
    {
        var emit = Volatile.Read(ref _emitSystem);
        if (emit == null)
            return false;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var message = text.Trim();
        if (message.Length > MaxMessageChars)
            message = message[..MaxMessageChars];

        var sourceName = string.IsNullOrWhiteSpace(source) ? "Plugin" : source.Trim();
        if (sourceName.Length > 64)
            sourceName = sourceName[..64];

        lock (Sync)
        {
            var now = DateTime.UtcNow;
            if (_windowStartUtc == DateTime.MinValue || (now - _windowStartUtc).TotalSeconds >= WindowSeconds)
            {
                _windowStartUtc = now;
                _sentInWindow = 0;
            }

            if (_sentInWindow >= MaxMessagesPerWindow)
                return false;

            _sentInWindow++;
        }

        return emit(message, sourceName);
    }
}
