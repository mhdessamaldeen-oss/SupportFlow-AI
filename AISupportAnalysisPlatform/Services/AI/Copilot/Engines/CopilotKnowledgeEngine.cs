using Microsoft.Extensions.Options;
using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Consolidated interface for all static and configured Copilot knowledge, 
    /// including prompts, heuristics, and messages.
    /// </summary>
    public interface ICopilotKnowledgeEngine
    {
        CopilotTextCatalog Prompts { get; }
        CopilotMessageCatalog Messages { get; }
        CopilotHeuristicCatalog Heuristics { get; }
    }

    public sealed class CopilotKnowledgeEngine : ICopilotKnowledgeEngine
    {
        public CopilotTextCatalog Prompts { get; }
        public CopilotMessageCatalog Messages { get; }
        public CopilotHeuristicCatalog Heuristics { get; }

        public CopilotKnowledgeEngine(
            CopilotTextCatalog prompts,
            CopilotMessageCatalog messages,
            CopilotHeuristicCatalog heuristics)
        {
            Prompts = prompts;
            Messages = messages;
            Heuristics = heuristics;
        }
    }
}
