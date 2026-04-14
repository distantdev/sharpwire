using System;
using System.Text;
using Microsoft.Extensions.AI;

namespace Sharpwire.Core;

/// <summary>
/// <see cref="ChatMessage.Text"/> only concatenates <see cref="TextContent"/> items. Some providers (e.g. Gemini via MEAI)
/// place visible assistant output in <see cref="DataContent"/> (text/*) or only expose <see cref="TextReasoningContent"/>,
/// which leaves <c>.Text</c> empty and makes the chat UI look blank even though the model replied.
/// The same applies to streaming: <see cref="ChatResponseUpdate.Text"/> may be empty while <see cref="ChatResponseUpdate.Contents"/> carries deltas.
/// </summary>
public static class ChatMessageTextExtractor
{
    /// <summary>Visible text for one streaming update (Gemini often populates <see cref="ChatResponseUpdate.Contents"/> only).</summary>
    public static string GetCombinedText(ChatResponseUpdate update)
    {
        if (update is null)
            return string.Empty;
        if (!string.IsNullOrEmpty(update.Text))
            return update.Text;
        if (update.Contents is { Count: > 0 })
            return GetCombinedText(new ChatMessage(ChatRole.Assistant, update.Contents));
        return string.Empty;
    }

    public static string GetCombinedText(ChatMessage message)
    {
        if (message.Contents is not { Count: > 0 })
            return message.Text ?? string.Empty;

        var sb = new StringBuilder();

        void AppendPart(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return;
            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append(s);
        }

        foreach (var c in message.Contents)
        {
            if (c is TextContent tc)
                AppendPart(tc.Text);
        }

        if (sb.Length == 0)
        {
            foreach (var c in message.Contents)
            {
                if (c is DataContent dc && IsTextLikeMediaType(dc.MediaType) && TryDecodeUtf8Body(dc, out var decoded))
                    AppendPart(decoded);
            }
        }

        if (sb.Length == 0)
        {
            foreach (var c in message.Contents)
            {
                if (c is TextReasoningContent trc)
                    AppendPart(trc.Text);
            }
        }

        if (sb.Length > 0)
            return sb.ToString();

        return message.Text ?? string.Empty;
    }

    private static bool IsTextLikeMediaType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
            return false;
        var m = mediaType.Trim().ToLowerInvariant();
        if (m.StartsWith("text/", StringComparison.Ordinal))
            return true;
        return m is "application/json" or "application/xml" or "application/javascript"
            || m.Contains("markdown", StringComparison.Ordinal);
    }

    private static bool TryDecodeUtf8Body(DataContent dc, out string text)
    {
        text = string.Empty;
        try
        {
            var mem = dc.Data;
            if (mem.IsEmpty)
                return false;
            if (mem.Length > 2_000_000)
                return false;

            text = Encoding.UTF8.GetString(mem.Span);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
