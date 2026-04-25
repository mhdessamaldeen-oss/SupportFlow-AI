using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI.Contracts
{
    public interface ICopilotPlanEngine
    {
        Task<CopilotExecutionPlan> BuildAsync(CopilotChatRequest request, CopilotQuestionContext questionContext, CopilotIntentDecision decision, CancellationToken cancellationToken = default);
    }

    public interface ICopilotExecutionEngine
    {
        Task<CopilotExecutionResult> ExecuteAsync(CopilotChatRequest request, CopilotQuestionContext questionContext, CopilotExecutionPlan plan, CancellationToken cancellationToken = default);
    }

    public interface ICopilotChatEngine
    {
        Task<CopilotChatResponse> AskAsync(CopilotChatRequest request, CancellationToken cancellationToken = default);
    }

    public interface ICopilotQuestionPreprocessor
    {
        CopilotQuestionContext Process(CopilotChatRequest request, CopilotConversationContext? conversationContext = null);
    }

    public interface ICopilotToolIntentResolver
    {
        Task<CopilotToolResolution> ResolveAsync(string question, CancellationToken cancellationToken = default);
    }

    public interface ICopilotIntentClassifierService
    {
        Task<CopilotIntentDecision> ClassifyAsync(CopilotQuestionContext questionContext, List<CopilotChatMessage>? history = null, CancellationToken cancellationToken = default);
    }

    public interface ICopilotVerificationEngine
    {
        CopilotVerificationResult Verify(CopilotExecutionPlan plan, CopilotExecutionResult execution);
    }

    public interface ICopilotConversationContextService
    {
        Task<CopilotConversationContext> BuildAsync(CopilotChatRequest request, CancellationToken cancellationToken = default);
    }
}
