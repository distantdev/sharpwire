using System.Collections.Generic;
using System.Linq;
using System.Text;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Sharpwire.Core.Security;

namespace Sharpwire.Core;

/// <summary>
/// MEAI Gemini mapping occasionally yields an empty <see cref="ChatRole.Assistant"/> shell while the underlying
/// <see cref="GenerateContentResponse"/> still exposes <see cref="GenerateContentResponse.Text"/> / function-call parts (debug logs: rawReprType Google.GenAI.Types.GenerateContentResponse, assistant Contents empty).
/// </summary>
public static class GeminiRawResponseInterop
{
    public static bool TryGetAggregateText(ChatResponse chatResponse, out string text) =>
        TryGetAggregateText(chatResponse, out text, out _);

    /// <param name="usedPartWalkFallback">True when <see cref="GenerateContentResponse.Text"/> was empty but non-empty <see cref="Part.Text"/> existed on the first candidate (e.g. thought-only segments excluded from aggregate).</param>
    public static bool TryGetAggregateText(ChatResponse chatResponse, out string text, out bool usedPartWalkFallback)
    {
        text = string.Empty;
        usedPartWalkFallback = false;
        if (chatResponse.RawRepresentation is not GenerateContentResponse g)
            return false;

        var t = g.Text;
        if (!string.IsNullOrWhiteSpace(t))
        {
            text = ChatTextNormalizer.ForDisplay(LogRedaction.MaskForUi(t));
            return !string.IsNullOrWhiteSpace(text);
        }

        if (TryConcatenateFirstCandidatePartTexts(g, out var walked))
        {
            text = walked;
            usedPartWalkFallback = true;
            return true;
        }

        return false;
    }

    private static bool TryConcatenateFirstCandidatePartTexts(GenerateContentResponse g, out string text)
    {
        text = string.Empty;
        var cand = g.Candidates?.FirstOrDefault();
        var parts = cand?.Content?.Parts;
        if (parts == null || parts.Count == 0)
            return false;

        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p.Text))
                continue;
            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append(p.Text);
        }

        var raw = sb.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        text = ChatTextNormalizer.ForDisplay(LogRedaction.MaskForUi(raw));
        return !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>
    /// When MEAI maps an empty assistant <see cref="ChatMessage"/> but <see cref="GenerateContentResponse.Text"/> is populated,
    /// replace the first empty assistant row so transcript + <see cref="ChatResponse"/> consumers see the same text.
    /// </summary>
    public static List<ChatMessage> PatchEmptyAssistantsFromGeminiAggregate(ChatResponse response, out bool patched)
    {
        patched = false;
        var list = response.Messages.ToList();
        if (!TryGetAggregateText(response, out var gx, out _))
            return list;

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Role != ChatRole.Assistant)
                continue;
            var combined = ChatMessageTextExtractor.GetCombinedText(list[i]);
            if (!string.IsNullOrWhiteSpace(ChatTextNormalizer.ForDisplay(combined)))
                continue;
            list[i] = new ChatMessage(ChatRole.Assistant, gx);
            patched = true;
            break;
        }

        return list;
    }
}
