using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotIntentClassifierService : ICopilotIntentClassifierService
    {
        private readonly ICopilotToolIntentResolver _toolIntentResolver;
        private readonly CopilotTextCatalog _text;

        public CopilotIntentClassifierService(
            ICopilotToolIntentResolver toolIntentResolver,
            CopilotTextCatalog text)
        {
            _toolIntentResolver = toolIntentResolver;
            _text = text;
        }

        public async Task<CopilotIntentDecision> ClassifyAsync(
            CopilotQuestionContext questionContext,
            List<CopilotChatMessage>? history = null,
            CancellationToken cancellationToken = default)
        {
            var immediateDecision = TryResolveImmediateDecision(questionContext);
            if (immediateDecision != null)
            {
                return immediateDecision;
            }

            var resolvedTool = await ResolveToolAsync(questionContext, cancellationToken);
            var toolDecision = TryResolveToolDecision(questionContext, resolvedTool);
            if (toolDecision != null)
            {
                return toolDecision;
            }

            return BuildDecision(
                CopilotIntentKind.GeneralChat,
                CopilotDataSource.None,
                RoutingConfidence.Medium,
                _text.ClassificationFallbackReason);
        }

        // Resolve the obvious local routes first so common admin prompts never pay the tool/model cost.
        private CopilotIntentDecision? TryResolveImmediateDecision(CopilotQuestionContext questionContext)
        {
            return TryResolveTicketDecision(questionContext)
                ?? TryResolveConversationDecision(questionContext)
                ?? TryResolveDataDecision(questionContext)
                ?? TryResolveGuidanceDecision(questionContext)
                ?? TryResolveSurfaceDecision(questionContext);
        }

        private CopilotIntentDecision? TryResolveConversationDecision(CopilotQuestionContext questionContext)
        {
            if (questionContext.LooksLikeGreeting)
            {
                return BuildDecision(
                    CopilotIntentKind.GeneralChat,
                    CopilotDataSource.None,
                    RoutingConfidence.VeryHigh,
                    _text.GreetingReason);
            }

            if (!questionContext.HasTicketReference &&
                questionContext.IsFollowUpQuestion &&
                questionContext.ConversationContext.PreviousQueryPlan != null)
            {
                return BuildDecision(
                    CopilotIntentKind.DataQuery,
                    CopilotDataSource.Database,
                    RoutingConfidence.High,
                    _text.FollowUpAnalyticsReason);
            }

            return null;
        }

        private CopilotIntentDecision? TryResolveTicketDecision(CopilotQuestionContext questionContext)
        {
            if (questionContext.HasTicketReference)
            {
                return BuildDecision(
                    CopilotIntentKind.DataQuery,
                    CopilotDataSource.Database,
                    RoutingConfidence.High,
                    _text.TicketReferenceReason);
            }

            return null;
        }

        private CopilotIntentDecision? TryResolveDataDecision(CopilotQuestionContext questionContext)
        {
            if (ShouldTreatAsDataQuery(questionContext))
            {
                return BuildDecision(
                    CopilotIntentKind.DataQuery,
                    CopilotDataSource.Database,
                    RoutingConfidence.High,
                    _text.StructuredDataReason);
            }

            return null;
        }

        private CopilotIntentDecision? TryResolveGuidanceDecision(CopilotQuestionContext questionContext)
        {
            if (questionContext.LooksLikeGuidanceQuestion)
            {
                return BuildDecision(
                    CopilotIntentKind.GeneralChat,
                    CopilotDataSource.None,
                    RoutingConfidence.High,
                    _text.RoutingKnowledgeBaseQueryReason);
            }

            return null;
        }

        private CopilotIntentDecision? TryResolveToolDecision(
            CopilotQuestionContext questionContext,
            CopilotToolResolution resolvedTool)
        {
            if (ShouldPreferToolRoute(questionContext, resolvedTool))
            {
                return BuildToolDecision(resolvedTool);
            }

            return resolvedTool.IsMatch ? BuildToolDecision(resolvedTool) : null;
        }

        private CopilotIntentDecision? TryResolveSurfaceDecision(CopilotQuestionContext questionContext)
        {
            if (string.Equals(questionContext.Surface, "reports", StringComparison.OrdinalIgnoreCase))
            {
                return BuildDecision(
                    CopilotIntentKind.DataQuery,
                    CopilotDataSource.Database,
                    RoutingConfidence.High,
                    $"{_text.StructuredDataReason} Reporting surface prefers analytics planning.");
            }

            return null;
        }

        private async Task<CopilotToolResolution> ResolveToolAsync(
            CopilotQuestionContext questionContext,
            CancellationToken cancellationToken)
        {
            return await _toolIntentResolver.ResolveAsync(questionContext.OriginalQuestion, cancellationToken);
        }

        private static bool ShouldPreferToolRoute(CopilotQuestionContext questionContext, CopilotToolResolution resolution)
        {
            if (!resolution.IsMatch ||
                questionContext.HasTicketReference ||
                ShouldTreatAsDataQuery(questionContext) ||
                questionContext.IsFollowUpQuestion)
            {
                return false;
            }

            return resolution.Confidence >= RoutingConfidence.High;
        }

        private static bool ShouldTreatAsDataQuery(CopilotQuestionContext questionContext)
        {
            if (questionContext.HasTicketReference || questionContext.LooksLikeDataQuestion)
            {
                return true;
            }

            return questionContext.IsFollowUpQuestion &&
                   questionContext.ConversationContext.PreviousQueryPlan != null;
        }

        private static CopilotIntentDecision BuildDecision(
            CopilotIntentKind intent,
            CopilotDataSource primarySource,
            RoutingConfidence confidence,
            string reason)
        {
            return new CopilotIntentDecision
            {
                Intent = intent,
                PrimarySource = primarySource,
                Confidence = confidence,
                Reason = reason
            };
        }

        private static CopilotIntentDecision BuildToolDecision(CopilotToolResolution resolvedTool)
        {
            return new CopilotIntentDecision
            {
                Intent = CopilotIntentKind.ExternalToolQuery,
                PrimarySource = CopilotDataSource.ExternalTool,
                Confidence = resolvedTool.Confidence,
                Reason = resolvedTool.Reason,
                ToolName = resolvedTool.ToolName,
                ToolQuery = resolvedTool.SearchText,
                ToolParameters = resolvedTool.Parameters,
                RequiresClarification = resolvedTool.RequiresClarification,
                ClarificationQuestion = resolvedTool.ClarificationQuestion
            };
        }
    }
}
