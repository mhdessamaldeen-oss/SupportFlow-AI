using System.Text.Json;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotConversationContextService : ICopilotConversationContextService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly CopilotHeuristicCatalog _heuristics;
        private readonly ILogger<CopilotConversationContextService> _logger;

        public CopilotConversationContextService(
            IDbContextFactory<ApplicationDbContext> contextFactory,
            CopilotHeuristicCatalog heuristics,
            ILogger<CopilotConversationContextService> logger)
        {
            _contextFactory = contextFactory;
            _heuristics = heuristics;
            _logger = logger;
        }

        public async Task<CopilotConversationContext> BuildAsync(CopilotChatRequest request, CancellationToken cancellationToken = default)
        {
            var recentQuestions = request.History
                .Where(m => m.Role == ChatMessageRole.User)
                .TakeLast(4)
                .Select(m => m.Content)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();

            var context = new CopilotConversationContext
            {
                LastTraceId = request.LastTraceId,
                RecentUserQuestions = recentQuestions
            };

            if (!request.LastTraceId.HasValue)
            {
                context.HasPriorContext = recentQuestions.Count > 0;
                context.Summary = BuildHistoryOnlySummary(recentQuestions);
                context.IsFollowUpCandidate = LooksLikeFollowUp(request.Question, context, _heuristics);
                return context;
            }

            using var dbContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var trace = await dbContext.CopilotTraceHistories
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == request.LastTraceId.Value, cancellationToken);

            if (trace == null)
            {
                context.HasPriorContext = recentQuestions.Count > 0;
                context.Summary = BuildHistoryOnlySummary(recentQuestions);
                context.IsFollowUpCandidate = LooksLikeFollowUp(request.Question, context, _heuristics);
                return context;
            }

            var details = DeserializeExecutionDetails(trace.ExecutionDetailsJson);
            context.HasPriorContext = true;
            context.LastQuestion = trace.Question;
            context.LastAnswerExcerpt = Trim(trace.Answer, 220);
            context.LastIntent = trace.Intent ?? details?.DetectedIntent ?? "";
            context.PreviousQueryPlan = details?.QueryPlan;
            context.PreviousActionPlan = details?.ActionPlan;
            context.LastTicketNumber = details?.QueryPlan?.TicketNumber ?? "";
            context.IsFollowUpCandidate = LooksLikeFollowUp(request.Question, context, _heuristics);
            context.Summary = BuildSummary(trace, details, recentQuestions, context.IsFollowUpCandidate);
            return context;
        }

        private static bool LooksLikeFollowUp(string question, CopilotConversationContext context, CopilotHeuristicCatalog heuristics)
        {
            if (!context.HasPriorContext && !context.LastTraceId.HasValue)
            {
                return false;
            }

            var normalized = Normalize(question);
            var tokenCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (tokenCount == 0)
            {
                return false;
            }

            // High-confidence follow-up markers (e.g. "and", "why", "then")
            if (heuristics.ConversationFollowUpPhrases.Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // Short questions are often follow-ups (e.g. "Status?", "Which entity?")
            if (tokenCount <= 5)
            {
                if (context.PreviousQueryPlan != null &&
                    heuristics.ConversationDeltaPhrases.Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(context.LastTicketNumber) &&
                    heuristics.TicketFollowUpSignals.Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private AdminCopilotExecutionDetails? DeserializeExecutionDetails(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize CopilotTraceHistory execution details. The schema may have changed since this trace was saved.");
                return null;
            }
        }

        private static string BuildSummary(
            CopilotTraceHistory trace, 
            AdminCopilotExecutionDetails? details, 
            IReadOnlyCollection<string> recentQuestions,
            bool isHighlyRelevant)
        {
            var parts = new List<string>();

            // If it's not a follow-up, we only include very lightweight context (e.g. recent questions)
            // to avoid confusing the AI with irrelevant "Last Answer" data.
            if (!isHighlyRelevant)
            {
                if (recentQuestions.Count > 0)
                {
                    parts.Add($"Recent user questions (for history): {string.Join(" | ", recentQuestions.Select(q => Trim(q, 80)))}");
                }
                return string.Join(". ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
            }

            if (!string.IsNullOrWhiteSpace(trace.Question))
            {
                parts.Add($"Last user request: {trace.Question}");
            }

            if (details?.QueryPlan != null)
            {
                var queryPlan = details.QueryPlan;
                parts.Add($"Last analytics data set: {queryPlan.TargetView ?? "TicketAnalytics"}");
                if (!string.IsNullOrWhiteSpace(queryPlan.EntityName))
                {
                    parts.Add($"Entity filter: {queryPlan.EntityName}");
                }
                if (queryPlan.StatusNames.Any())
                {
                    parts.Add($"Statuses: {string.Join(", ", queryPlan.StatusNames)}");
                }
            }

            if (details?.ActionPlan != null && details.ActionPlan.Intent != PlatformActionIntent.None)
            {
                parts.Add($"Last action plan: {details.ActionPlan.Intent}");
            }

            if (!string.IsNullOrWhiteSpace(trace.Answer))
            {
                parts.Add($"Last answer excerpt: {Trim(trace.Answer, 220)}");
            }

            if (recentQuestions.Count > 0)
            {
                parts.Add($"Full history: {string.Join(" | ", recentQuestions.Select(q => Trim(q, 80)))}");
            }

            return string.Join(". ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private static string BuildHistoryOnlySummary(IReadOnlyCollection<string> recentQuestions)
        {
            if (recentQuestions.Count == 0)
            {
                return "";
            }

            return $"Recent user questions: {string.Join(" | ", recentQuestions.Select(q => Trim(q, 80)))}";
        }

        private static string Normalize(string text)
            => string.Join(' ', (text ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        private static string Trim(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            return text.Length <= max ? text : text[..max] + "...";
        }
    }
}
