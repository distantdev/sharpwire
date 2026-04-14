using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Sharpwire.Core.Workflow;

public static class WorkflowReviewParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static WorkflowReviewResult? TryParseReview(string agentName, string assistantText, bool isReviewer, ChatResponse response)
    {
        if (!isReviewer)
            return null;

        // Check if the ApproveWork tool was called in the message history of this response.
        bool wasApproved = response.Messages.Any(m => 
            m.Contents.OfType<FunctionCallContent>()
            .Any(f => string.Equals(f.Name, "ApproveWork", StringComparison.OrdinalIgnoreCase)));

        if (wasApproved)
        {
            return new WorkflowReviewResult { Status = "Passed", Critique = null };
        }

        var trimmed = assistantText.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ReviewJsonDto>(trimmed, JsonOpts);
                if (dto?.Status != null)
                {
                    return new WorkflowReviewResult
                    {
                        Status = dto.Status,
                        Critique = dto.Critique
                    };
                }
            }
            catch
            {
                /* fall through */
            }
        }

        return new WorkflowReviewResult
        {
            Status = "Failed",
            Critique = string.IsNullOrWhiteSpace(trimmed) ? "(empty reviewer reply)" : trimmed
        };
    }

    private sealed class ReviewJsonDto
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("critique")]
        public string? Critique { get; set; }
    }
}
