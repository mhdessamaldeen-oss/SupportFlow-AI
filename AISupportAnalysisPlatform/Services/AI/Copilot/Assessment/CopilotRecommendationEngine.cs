using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Providers;
using System.Text.Json;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotRecommendationEngine
    {
        private readonly TicketContextPreparationService _contextPreparationService;
        private readonly ISemanticSearchService _semanticSearchService;
        private readonly KnowledgeBaseRagService _knowledgeBaseRagService;
        private readonly IAiProviderFactory _providerFactory;
        private readonly CopilotTextCatalog _text;
        private readonly ILogger<CopilotRecommendationEngine> _logger;

        public CopilotRecommendationEngine(
            TicketContextPreparationService contextPreparationService,
            ISemanticSearchService semanticSearchService,
            KnowledgeBaseRagService knowledgeBaseRagService,
            IAiProviderFactory providerFactory,
            CopilotTextCatalog text,
            ILogger<CopilotRecommendationEngine> logger)
        {
            _contextPreparationService = contextPreparationService;
            _semanticSearchService = semanticSearchService;
            _knowledgeBaseRagService = knowledgeBaseRagService;
            _providerFactory = providerFactory;
            _text = text;
            _logger = logger;
        }

        public async Task<CopilotTicketRecommendation> GenerateAsync(int ticketId)
        {
            var context = await _contextPreparationService.PrepareAsync(ticketId);
            var similarTickets = await _semanticSearchService.GetRelatedTicketsAsync(ticketId, count: 3);
            var knowledgeMatches = await _knowledgeBaseRagService.SearchAsync(BuildKnowledgeQuery(context), count: 3);
            var provider = _providerFactory.GetActiveProvider();

            var recommendation = new CopilotTicketRecommendation
            {
                TicketId = ticketId,
                ModelName = provider.ModelName,
                KnowledgeDocumentCount = _knowledgeBaseRagService.GetDocumentCount(),
                SimilarTickets = similarTickets.Select(m => new CopilotTicketCitation
                {
                    TicketId = m.Ticket.Id,
                    TicketNumber = m.Ticket.TicketNumber,
                    Title = m.Ticket.Title,
                    ResolutionSummary = m.Ticket.ResolutionSummary ?? "",
                    RootCause = m.Ticket.RootCause ?? "",
                    Status = m.Ticket.Status?.Name ?? "",
                    Score = m.Score
                }).ToList(),
                KnowledgeMatches = knowledgeMatches
            };

            try
            {
                var prompt = BuildEvidenceBasePrompt(context, recommendation);
                var result = await provider.GenerateAsync(prompt);
                if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
                {
                    recommendation.GenerationNotes = result.Error ?? "AI provider did not return a Copilot recommendation.";
                    ApplyFallbackRecommendation(recommendation);
                    return recommendation;
                }

                ApplyProviderResponse(recommendation, result.ResponseText);
                if (string.IsNullOrWhiteSpace(recommendation.Summary) || string.IsNullOrWhiteSpace(recommendation.RecommendedAction))
                {
                    ApplyFallbackRecommendation(recommendation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Copilot recommendation generation failed for ticket {TicketId}", ticketId);
                recommendation.GenerationNotes = ex.Message;
                ApplyFallbackRecommendation(recommendation);
            }

            return recommendation;
        }

        private static string BuildKnowledgeQuery(TicketContext context)
        {
            var parts = new[]
            {
                context.Title,
                context.Description,
                context.ProductArea,
                context.EnvironmentName,
                context.TechnicalAssessment,
                context.PendingReason,
                context.RootCause,
                context.ResolutionSummary,
                context.CommentsText
            };

            return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private string BuildEvidenceBasePrompt(TicketContext context, CopilotTicketRecommendation recommendation)
        {
            var similarTickets = recommendation.SimilarTickets.Any()
                ? string.Join("\n\n", recommendation.SimilarTickets.Select(t =>
                    $"Ticket {t.TicketNumber} | Score {(t.Score * 100):0.0}% | Status {t.Status}\nTitle: {t.Title}\nResolution Summary: {t.ResolutionSummary}\nRoot Cause: {t.RootCause}"))
                : "No strong similar resolved tickets were found.";

            var knowledgeChunks = recommendation.KnowledgeMatches.Any()
                ? string.Join("\n\n", recommendation.KnowledgeMatches.Select(k =>
                    $"Document: {k.DocumentName}\nSection: {k.SectionTitle}\nScore: {(k.Score * 100):0.0}%\nExcerpt: {k.Excerpt}"))
                : "No strong internal knowledge documents were found.";
            return CopilotTextTemplate.Apply(
                CopilotTextTemplate.JoinLines(_text.RecommendationEvidencePromptLines),
                new Dictionary<string, string?>
                {
                    ["TICKET_NUMBER"] = context.TicketNumber,
                    ["TITLE"] = context.Title,
                    ["DESCRIPTION"] = context.Description,
                    ["CATEGORY"] = context.Category,
                    ["PRIORITY"] = context.Priority,
                    ["STATUS"] = context.Status,
                    ["PRODUCT_AREA"] = context.ProductArea,
                    ["ENVIRONMENT"] = context.EnvironmentName,
                    ["COMMENTS"] = context.CommentsText,
                    ["SIMILAR_TICKETS"] = similarTickets,
                    ["KNOWLEDGE_CHUNKS"] = knowledgeChunks
                });
        }

        private static void ApplyProviderResponse(CopilotTicketRecommendation recommendation, string responseText)
        {
            try
            {
                var cleanJson = responseText.Trim();
                var startIdx = cleanJson.IndexOf('{');
                var endIdx = cleanJson.LastIndexOf('}');
                if (startIdx >= 0 && endIdx > startIdx)
                {
                    cleanJson = cleanJson[startIdx..(endIdx + 1)];
                }

                using var doc = JsonDocument.Parse(cleanJson);
                recommendation.Summary = doc.RootElement.TryGetProperty("summary", out var summary)
                    ? summary.GetString() ?? ""
                    : "";
                recommendation.RecommendedAction = doc.RootElement.TryGetProperty("recommendedAction", out var action)
                    ? action.GetString() ?? ""
                    : "";
                recommendation.EvidenceStrength = doc.RootElement.TryGetProperty("evidenceStrength", out var strength)
                    ? strength.GetString() ?? "Weak"
                    : "Weak";
            }
            catch (JsonException)
            {
                recommendation.GenerationNotes = "Copilot response parsing failed. Fallback explanation applied.";
            }
        }

        private static void ApplyFallbackRecommendation(CopilotTicketRecommendation recommendation)
        {
            var strongestTicket = recommendation.SimilarTickets.OrderByDescending(t => t.Score).FirstOrDefault();
            var strongestDoc = recommendation.KnowledgeMatches.OrderByDescending(k => k.Score).FirstOrDefault();

            recommendation.Summary = strongestTicket != null
                ? $"The strongest evidence points to a case similar to {strongestTicket.TicketNumber}, which appears to have been resolved through the cited workflow evidence."
                : strongestDoc != null
                    ? $"The strongest evidence comes from the internal knowledge document '{strongestDoc.DocumentName}' in section '{strongestDoc.SectionTitle}'."
                    : "No strong evidence was available to produce a valid copilot recommendation.";

            recommendation.RecommendedAction = strongestTicket != null || strongestDoc != null
                ? "Review the cited tickets and knowledge excerpts before applying a support action to this ticket."
                : "Add internal knowledge documents or resolved tickets before relying on an AI recommendation.";

            recommendation.EvidenceStrength = strongestTicket != null && strongestDoc != null
                ? "Moderate"
                : strongestTicket != null || strongestDoc != null
                    ? "Weak"
                    : "Weak";
        }
    }
}
