using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI.Contracts
{
    /// <summary>
    /// Pillar 1: The Brain. Responsible for preprocessing, intent classification, 
    /// and fuzzy semantic understanding.
    /// </summary>
    public interface ICopilotIntelligenceEngine
    {
        /// <summary>
        /// Analyzes a raw request to produce a unified intelligence context. 
        /// Uses weighted fuzzy scoring to handle varied phrasing.
        /// </summary>
        Task<CopilotIntelligenceContext> AnalyzeAsync(
            CopilotChatRequest request, 
            CopilotConversationContext? conversationContext = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Unified context containing everything the Copilot understands about the question.
    /// </summary>
    public class CopilotIntelligenceContext
    {
        public CopilotQuestionContext QuestionContext { get; set; } = new();
        public CopilotIntentDecision IntentDecision { get; set; } = new();
        
        /// <summary>
        /// A score representing how confident the fuzzy rule-based engine is.
        /// If low, the system may decide to use LLM-based classification as a fallback.
        /// </summary>
        public double RuleConfidenceScore { get; set; }
    }
}
