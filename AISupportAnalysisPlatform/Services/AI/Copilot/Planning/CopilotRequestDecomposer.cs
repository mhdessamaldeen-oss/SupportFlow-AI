using System.Text.RegularExpressions;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Splits one user message into independent executable parts.
    /// A prompt can legitimately contain data questions, external tool calls, and normal chat in the same message.
    /// </summary>
    public class CopilotRequestDecomposer
    {
        private const int MaxSubRequests = 6;
        private static readonly string[] StrongSeparators = ["also", "then", "plus", "ثم", "كذلك"];
        private readonly ICopilotQuestionPreprocessor _preprocessor;
        private readonly ICopilotToolIntentResolver _toolIntentResolver;

        public CopilotRequestDecomposer(
            ICopilotQuestionPreprocessor preprocessor,
            ICopilotToolIntentResolver toolIntentResolver)
        {
            _preprocessor = preprocessor;
            _toolIntentResolver = toolIntentResolver;
        }

        /// <summary>
        /// Builds the sub-request list used by planning.
        /// External tools are selected from configured tool metadata; data hints are only a fast pre-filter before catalog validation.
        /// </summary>
        public async Task<CopilotRequestDecomposition> DecomposeAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotIntentDecision primaryDecision,
            CancellationToken cancellationToken = default)
        {
            var fragments = ExtractFragments(questionContext);

            // A single-route prompt should reuse the already-selected primary decision.
            // Reclassifying the same text here only repeats tool/model work and adds latency.
            if (fragments.Count <= 1)
            {
                return new CopilotRequestDecomposition
                {
                    SubRequests = [BuildFromPrimaryDecision(questionContext.OriginalQuestion, primaryDecision)]
                };
            }

            var subRequests = new List<CopilotSubRequest>();

            foreach (var fragment in fragments.Take(MaxSubRequests))
            {
                subRequests.Add(await ClassifyFragmentAsync(request, fragment, questionContext, cancellationToken));
            }

            if (subRequests.Count == 0)
            {
                subRequests.Add(BuildFromPrimaryDecision(request.Question, primaryDecision));
            }

            if (subRequests.Count == 1 && primaryDecision.Intent != subRequests[0].Kind)
            {
                subRequests[0] = BuildFromPrimaryDecision(subRequests[0].Text, primaryDecision);
            }

            return new CopilotRequestDecomposition { SubRequests = subRequests };
        }

        private async Task<CopilotSubRequest> ClassifyFragmentAsync(
            CopilotChatRequest request,
            string fragment,
            CopilotQuestionContext parentContext,
            CancellationToken cancellationToken)
        {
            var context = _preprocessor.Process(new CopilotChatRequest
            {
                Question = fragment,
                Surface = request.Surface,
                History = request.History,
                ReportStartDate = request.ReportStartDate,
                ReportEndDate = request.ReportEndDate
            }, parentContext.ConversationContext);

            // Keep admin platform data on the catalog route before evaluating external tools.
            if (ShouldTreatAsDataFragment(context))
            {
                return new CopilotSubRequest
                {
                    Id = NewId(),
                    Text = fragment,
                    Kind = CopilotIntentKind.DataQuery,
                    Source = CopilotDataSource.Database,
                    Confidence = RoutingConfidence.High,
                    Reason = "Fragment asks for application data and will be validated against the data catalog."
                };
            }

            if (context.LooksLikeGreeting || context.LooksLikeGuidanceQuestion)
            {
                return new CopilotSubRequest
                {
                    Id = NewId(),
                    Text = fragment,
                    Kind = CopilotIntentKind.GeneralChat,
                    Source = CopilotDataSource.None,
                    Confidence = context.LooksLikeGreeting ? RoutingConfidence.VeryHigh : RoutingConfidence.High,
                    Reason = context.LooksLikeGreeting
                        ? "Fragment is a greeting or capability prompt and does not require tool routing."
                        : "Fragment is a guidance-style conversation prompt and does not require tool routing."
                };
            }

            var tool = await _toolIntentResolver.ResolveAsync(fragment, cancellationToken);
            if (tool.IsMatch && tool.Confidence >= RoutingConfidence.High && !context.IsFollowUpQuestion)
            {
                return new CopilotSubRequest
                {
                    Id = NewId(),
                    Text = fragment,
                    Kind = CopilotIntentKind.ExternalToolQuery,
                    Source = CopilotDataSource.ExternalTool,
                    Confidence = tool.Confidence,
                    Reason = tool.Reason,
                    ToolName = tool.ToolName,
                    ToolQuery = tool.SearchText,
                    ToolParameters = tool.Parameters,
                    RequiresClarification = tool.RequiresClarification,
                    ClarificationQuestion = tool.ClarificationQuestion
                };
            }

            return new CopilotSubRequest
            {
                Id = NewId(),
                Text = fragment,
                Kind = CopilotIntentKind.GeneralChat,
                Source = CopilotDataSource.None,
                Confidence = context.LooksLikeGreeting ? RoutingConfidence.VeryHigh : RoutingConfidence.Medium,
                Reason = "Fragment does not require database access or an external tool."
            };
        }

        private List<string> ExtractFragments(CopilotQuestionContext questionContext)
        {
            var rawParts = questionContext.QueryParts.Count > 0
                ? questionContext.QueryParts
                : [questionContext.OriginalQuestion];

            return rawParts
                .SelectMany(SplitOnConnectors)
                .Select(CleanFragment)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private IEnumerable<string> SplitOnConnectors(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return [];
            }

            var fragments = new List<string> { input.Trim() };

            foreach (var separator in StrongSeparators)
            {
                var next = new List<string>();
                foreach (var fragment in fragments)
                {
                    next.AddRange(SplitOnStrongSeparator(fragment, separator));
                }

                fragments = next;
            }

            return fragments
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value));
        }

        private static IEnumerable<string> SplitOnStrongSeparator(string input, string separator)
        {
            var pattern = $@"\s+{Regex.Escape(separator)}\s+";
            return Regex.Split(input, pattern, RegexOptions.IgnoreCase)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value));
        }

        private static CopilotSubRequest BuildFromPrimaryDecision(string text, CopilotIntentDecision decision)
        {
            return new CopilotSubRequest
            {
                Id = NewId(),
                Text = text,
                Kind = decision.Intent,
                Source = decision.PrimarySource,
                Confidence = decision.Confidence,
                Reason = decision.Reason,
                ToolName = decision.ToolName,
                ToolQuery = decision.ToolQuery,
                ToolParameters = new Dictionary<string, string>(decision.ToolParameters, StringComparer.OrdinalIgnoreCase),
                RequiresClarification = decision.RequiresClarification,
                ClarificationQuestion = decision.ClarificationQuestion
            };
        }

        private static string CleanFragment(string fragment)
            => fragment.Trim().Trim(',', ';', ':', '.', '?', '!');

        private static bool ShouldTreatAsDataFragment(CopilotQuestionContext context)
            => context.HasTicketReference ||
               context.LooksLikeDataQuestion ||
               (context.IsFollowUpQuestion && context.ConversationContext.PreviousQueryPlan != null);

        private static string NewId()
            => $"step-{Guid.NewGuid():N}"[..13];
    }
}
