using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Intelligence stage for the copilot.
    /// Converts raw user text into a normalized question context and a first routing decision.
    /// </summary>
    public class CopilotIntelligenceEngine : ICopilotIntelligenceEngine
    {
        private readonly ICopilotKnowledgeEngine _knowledge;
        private readonly ICopilotIntentClassifierService _classifier;
        private readonly ICopilotQuestionPreprocessor _preprocessor;
        private readonly IOptionsMonitor<CopilotTextSettings> _settingsMonitor;
        private readonly ILogger<CopilotIntelligenceEngine> _logger;

        public CopilotIntelligenceEngine(
            ICopilotKnowledgeEngine knowledge,
            ICopilotIntentClassifierService classifier,
            ICopilotQuestionPreprocessor preprocessor,
            IOptionsMonitor<CopilotTextSettings> settingsMonitor,
            ILogger<CopilotIntelligenceEngine> logger)
        {
            _knowledge = knowledge;
            _classifier = classifier;
            _preprocessor = preprocessor;
            _settingsMonitor = settingsMonitor;
            _logger = logger;
        }

        public async Task<CopilotIntelligenceContext> AnalyzeAsync(
            CopilotChatRequest request,
            CopilotConversationContext? conversationContext = null,
            CancellationToken cancellationToken = default)
        {
            // Step 1: preprocess the raw request into a normalized question context.
            // This extracts the structural signals the rest of the copilot depends on.
            var questionContext = BuildQuestionContext(request, conversationContext);

            // Step 2: score the normalized question with cheap heuristic weights.
            // This is the fast local route that avoids a model call for obvious prompts.
            var heuristicScores = BuildHeuristicScorecard(questionContext);
            var (topIntent, maxScore) = SelectTopHeuristicIntent(heuristicScores);

            _logger.LogInformation("Intelligence Weighted Scoring: TopIntent={Intent}, Score={Score}", topIntent, maxScore);

            // Step 3: either trust the heuristic winner or fall back to the classifier.
            // Safety: if the preprocessor flagged the question as potentially unsafe (injection risk), block it.
            var decision = questionContext.IsPotentiallyUnsafe
                ? BuildBlockedDecision()
                : await ResolveIntentDecisionAsync(request, questionContext, topIntent, maxScore, cancellationToken);

            return new CopilotIntelligenceContext
            {
                QuestionContext = questionContext,
                IntentDecision = decision,
                RuleConfidenceScore = maxScore
            };
        }

        private CopilotQuestionContext BuildQuestionContext(
            CopilotChatRequest request,
            CopilotConversationContext? conversationContext)
        {
            // Preprocessor responsibilities:
            // - normalize the text
            // - expand synonyms / typo tolerance
            // - detect ticket references
            // - mark greeting / analytics / KB / follow-up hints
            return _preprocessor.Process(request, conversationContext);
        }

        private async Task<CopilotIntentDecision> ResolveIntentDecisionAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotIntentKind topIntent,
            double maxScore,
            CancellationToken cancellationToken)
        {
            if (questionContext.LooksLikeGreeting)
            {
                return BuildHeuristicDecision(CopilotIntentKind.GeneralChat, 1.0);
            }

            if (CanUseHeuristicDecision(topIntent, maxScore))
            {
                // Local fast-path: build the decision directly from the heuristic winner.
                return BuildHeuristicDecision(topIntent, maxScore);
            }

            // Fallback path: use the classifier when heuristics are weak or ambiguous.
            return await _classifier.ClassifyAsync(questionContext, request.History, cancellationToken);
        }

        private bool CanUseHeuristicDecision(CopilotIntentKind topIntent, double maxScore)
            => maxScore >= _settingsMonitor.CurrentValue.Routing.Weights.HeuristicBypassThreshold && topIntent != CopilotIntentKind.Unsupported;

        private Dictionary<CopilotIntentKind, double> BuildHeuristicScorecard(CopilotQuestionContext questionContext)
        {
            // Build weighted scores per major intent family.
            // These are not final answers; they are only a cheap confidence estimate for routing.
            var scores = new Dictionary<CopilotIntentKind, double>
            {
                [CopilotIntentKind.GeneralChat] = 0,
                [CopilotIntentKind.DataQuery] = 0,
                [CopilotIntentKind.ExternalToolQuery] = 0
            };

            var normalized = questionContext.NormalizedQuestion;
            var weights = _settingsMonitor.CurrentValue.Routing.Weights;

            // 1. Social/Greeting score:
            if (questionContext.LooksLikeGreeting) scores[CopilotIntentKind.GeneralChat] += weights.GreetingIndicator;

            return scores;
        }

        private static (CopilotIntentKind Intent, double Score) SelectTopHeuristicIntent(
            Dictionary<CopilotIntentKind, double> scores)
        {
            // Pick the highest local score. This is only a routing candidate, not the final answer payload.
            var top = scores.OrderByDescending(s => s.Value).FirstOrDefault();
            return (top.Key, Math.Min(top.Value, 1.0));
        }

        private static CopilotIntentDecision BuildHeuristicDecision(CopilotIntentKind intent, double score)
        {
            // Convert the winning heuristic bucket into the same decision model used by the rest of the pipeline.
            return new CopilotIntentDecision
            {
                Intent = intent,
                PrimarySource = ResolvePrimarySource(intent),
                Confidence = score >= 0.9 ? RoutingConfidence.High : RoutingConfidence.Medium,
                Reason = $"Heuristic weighted match (score: {score:P0})",
                ToolName = intent == CopilotIntentKind.ExternalToolQuery ? "Auto" : "none"
            };
        }

        private static CopilotDataSource ResolvePrimarySource(CopilotIntentKind intent)
            => intent switch
            {
                CopilotIntentKind.DataQuery => CopilotDataSource.Database,
                CopilotIntentKind.ExternalToolQuery => CopilotDataSource.ExternalTool,
                _ => CopilotDataSource.None
            };

        private CopilotIntentDecision BuildBlockedDecision()
        {
            return new CopilotIntentDecision
            {
                Intent = CopilotIntentKind.Unsupported,
                Confidence = RoutingConfidence.High,
                Reason = _knowledge.Messages.SecurityBlockReason,
                RequiresClarification = true,
                ClarificationQuestion = _knowledge.Messages.SecurityBlockMessage
            };
        }

        private static bool ContainsAny(string text, IEnumerable<string> phrases)
            => phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
