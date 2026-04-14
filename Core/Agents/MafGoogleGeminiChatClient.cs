using Google.GenAI;
using Microsoft.Extensions.AI;

namespace Sharpwire.Core.Agents;

/// <summary>
/// Gemini via <see cref="IChatClient"/> using the same stack as Microsoft's MAF sample
/// <c>Agent_With_GoogleGemini</c> (Google.GenAI <see cref="Client"/> + <c>AsIChatClient</c>),
/// plus MEAI <see cref="ChatClientBuilderExtensions.UseFunctionInvocation"/> for automatic tool invocation.
/// </summary>
internal static class MafGoogleGeminiChatClient
{
    public static IChatClient Create(string apiKey, string modelId)
    {
        var genAi = new Client(vertexAI: false, apiKey: apiKey);
        return genAi.AsIChatClient(modelId)
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }
}
