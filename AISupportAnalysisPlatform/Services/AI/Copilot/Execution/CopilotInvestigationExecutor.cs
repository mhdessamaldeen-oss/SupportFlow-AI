using System.Text;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using AISupportAnalysisPlatform.Services.AI.Providers;
using AutoMapper;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotInvestigationExecutor
    {
        private readonly TicketContextPreparationService _contextPreparationService;
        private readonly ISemanticSearchService _semanticSearchService;
        private readonly KnowledgeBaseRagService _knowledgeBaseRagService;
        private readonly IAiProviderFactory _providerFactory;
        private readonly CopilotTextCatalog _text;
        private readonly IMapper _mapper;

        public CopilotInvestigationExecutor(
            TicketContextPreparationService contextPreparationService,
            ISemanticSearchService semanticSearchService,
            KnowledgeBaseRagService knowledgeBaseRagService,
            IAiProviderFactory providerFactory,
            CopilotTextCatalog text,
            IMapper mapper)
        {
            _contextPreparationService = contextPreparationService;
            _semanticSearchService = semanticSearchService;
            _knowledgeBaseRagService = knowledgeBaseRagService;
            _providerFactory = providerFactory;
            _text = text;
            _mapper = mapper;
        }

        public async Task<CopilotExecutionResult> ExecuteAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotExecutionPlan plan,
            int? ticketId,
            CancellationToken cancellationToken = default)
        {
            var executionSteps = new List<CopilotExecutionStep>();

            TicketContext? ticketContext = null;
            if (ticketId.HasValue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ticketContext = await _contextPreparationService.PrepareAsync(ticketId.Value, cancellationToken);
                executionSteps.Add(new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.Executor,
                    Action = "Prepare Ticket Context [TicketContextPreparationService -> PrepareAsync]",
                    Detail = $"Input: Ticket ID {ticketId.Value}\nOutput: Ticket {ticketContext.TicketNumber} context loaded.",
                    Status = CopilotStepStatus.Ok
                });
            }

            var queryText = BuildEvidenceQuery(questionContext, ticketContext);
            cancellationToken.ThrowIfCancellationRequested();
            var knowledgeMatches = await _knowledgeBaseRagService.SearchAsync(queryText, count: 4, cancellationToken);
            executionSteps.Add(new CopilotExecutionStep
            {
                Layer = CopilotExecutionLayer.Executor,
                Action = "Knowledge Base RAG Search [KnowledgeBaseRagService -> SearchAsync]",
                Detail = $"Input: \"{queryText.Substring(0, Math.Min(queryText.Length, 100))}...\"\nOutput: Found {knowledgeMatches.Count} matches.",
                Status = knowledgeMatches.Any() ? CopilotStepStatus.Ok : CopilotStepStatus.Warn
            });

            cancellationToken.ThrowIfCancellationRequested();
            var similarMatches = ticketId.HasValue
                ? await _semanticSearchService.GetRelatedTicketsAsync(ticketId.Value, count: 4, cancellationToken: cancellationToken)
                : await _semanticSearchService.SearchSimilarTicketsByTextAsync(queryText, count: 4, cancellationToken: cancellationToken);
            
            var similarTickets = _mapper.Map<List<CopilotTicketCitation>>(similarMatches);
            executionSteps.Add(new CopilotExecutionStep
            {
                Layer = CopilotExecutionLayer.Executor,
                Action = "Semantic Similar Ticket Search [ISemanticSearchService -> GetRelatedTicketsAsync]",
                Detail = $"Input: Semantic features\nOutput: Found {similarTickets.Count} related tickets.",
                Status = similarTickets.Any() ? CopilotStepStatus.Ok : CopilotStepStatus.Warn
            });

            var prompt = BuildKnowledgeMatchPrompt(request, ticketContext, knowledgeMatches, similarTickets);
            var provider = _providerFactory.GetActiveProvider();
            var llm = await provider.GenerateAsync(prompt);

            executionSteps.Add(new CopilotExecutionStep
            {
                Layer = CopilotExecutionLayer.Executor,
                Action = "LLM Evidence Synthesis [CopilotInvestigationExecutor -> ExecuteAsync]",
                Detail = $"Input: Context + {knowledgeMatches.Count} KB items + {similarTickets.Count} Tickets\nOutput: Generated response summary.",
                Status = llm.Success ? CopilotStepStatus.Ok : CopilotStepStatus.Warn
            });

            var fallbackAnswer = BuildKnowledgeMatchFallback(ticketContext, knowledgeMatches, similarTickets);
            var answer = llm.Success && !string.IsNullOrWhiteSpace(llm.ResponseText)
                ? llm.ResponseText.Trim()
                : fallbackAnswer;

            return new CopilotExecutionResult
            {
                Answer = answer,
                Summary = CopilotTextTemplate.Apply(
                    _text.KnowledgeMatchExecutionSummaryTemplate,
                    new Dictionary<string, string?>
                    {
                        ["KNOWLEDGE_COUNT"] = knowledgeMatches.Count.ToString(),
                        ["SIMILAR_COUNT"] = similarTickets.Count.ToString()
                    }),
                ResultCount = knowledgeMatches.Count + similarTickets.Count,
                ResponseMode = ResponseMode.KnowledgeMatch,
                EvidenceStrength = knowledgeMatches.Any() || similarTickets.Any() ? EvidenceStrength.High : EvidenceStrength.Weak,
                KnowledgeMatches = knowledgeMatches,
                SimilarTickets = similarTickets,
                ExecutionSteps = executionSteps,
                SuggestedPrompts = BuildFollowUpPrompts(ticketContext, questionContext)
            };
        }

        private static string BuildEvidenceQuery(CopilotQuestionContext questionContext, TicketContext? ticketContext)
        {
            var parts = new List<string> { questionContext.SearchText };
            if (!string.IsNullOrWhiteSpace(questionContext.ConversationSummary))
            {
                parts.Add(questionContext.ConversationSummary);
            }
            if (ticketContext != null)
            {
                parts.Add(ticketContext.Title);
                parts.Add(ticketContext.Description);
                parts.Add(ticketContext.RootCause);
                parts.Add(ticketContext.ResolutionSummary);
            }

            return string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private string BuildKnowledgeMatchPrompt(
            CopilotChatRequest request,
            TicketContext? ticketContext,
            IReadOnlyCollection<KnowledgeBaseChunkMatch> knowledgeMatches,
            IReadOnlyCollection<CopilotTicketCitation> similarTickets)
        {
            var builder = new StringBuilder();
            foreach (var line in _text.KnowledgeMatchPromptIntroLines)
            {
                builder.AppendLine(line);
            }
            builder.AppendLine();
 
            if (ticketContext != null)
            {
                builder.AppendLine(_text.InvestigationPromptTicketContextLabel);
                builder.AppendLine(CopilotTextTemplate.Apply(_text.InvestigationPromptTicketLineTemplate, new Dictionary<string, string?> { ["TICKET_NUMBER"] = ticketContext.TicketNumber }));
                builder.AppendLine(CopilotTextTemplate.Apply(_text.InvestigationPromptTitleLineTemplate, new Dictionary<string, string?> { ["TITLE"] = ticketContext.Title }));
                builder.AppendLine(CopilotTextTemplate.Apply(_text.InvestigationPromptStatusLineTemplate, new Dictionary<string, string?> { ["STATUS"] = ticketContext.Status }));
                builder.AppendLine(CopilotTextTemplate.Apply(_text.InvestigationPromptPriorityLineTemplate, new Dictionary<string, string?> { ["PRIORITY"] = ticketContext.Priority }));
                if (!string.IsNullOrWhiteSpace(ticketContext.PendingReason))
                {
                    builder.AppendLine(CopilotTextTemplate.Apply(_text.InvestigationPromptPendingReasonLineTemplate, new Dictionary<string, string?> { ["PENDING_REASON"] = ticketContext.PendingReason }));
                }
                if (!string.IsNullOrWhiteSpace(ticketContext.TechnicalAssessment))
                {
                    builder.AppendLine(CopilotTextTemplate.Apply(_text.InvestigationPromptTechnicalAssessmentLineTemplate, new Dictionary<string, string?> { ["TECHNICAL_ASSESSMENT"] = ticketContext.TechnicalAssessment }));
                }
                if (!string.IsNullOrWhiteSpace(ticketContext.ResolutionSummary))
                {
                    builder.AppendLine(CopilotTextTemplate.Apply(_text.InvestigationPromptResolutionSummaryLineTemplate, new Dictionary<string, string?> { ["RESOLUTION_SUMMARY"] = ticketContext.ResolutionSummary }));
                }
                builder.AppendLine();
            }
 
            builder.AppendLine(_text.InvestigationPromptKnowledgeLabel);
            foreach (var match in knowledgeMatches)
            {
                builder.AppendLine(CopilotTextTemplate.Apply(
                    _text.InvestigationPromptKnowledgeItemTemplate,
                    new Dictionary<string, string?>
                    {
                        ["DOCUMENT_NAME"] = match.DocumentName,
                        ["SECTION_TITLE"] = match.SectionTitle,
                        ["EXCERPT"] = match.Excerpt
                    }));
            }
            builder.AppendLine();
 
            builder.AppendLine(_text.InvestigationPromptSimilarLabel);
            foreach (var ticket in similarTickets)
            {
                builder.AppendLine(CopilotTextTemplate.Apply(
                    _text.InvestigationPromptSimilarItemTemplate,
                    new Dictionary<string, string?>
                    {
                        ["TICKET_NUMBER"] = ticket.TicketNumber,
                        ["TITLE"] = ticket.Title,
                        ["RESOLUTION_SUMMARY"] = ticket.ResolutionSummary,
                        ["ROOT_CAUSE"] = ticket.RootCause
                    }));
            }
            builder.AppendLine();
            builder.AppendLine(CopilotTextTemplate.Apply(_text.InvestigationPromptUserQuestionTemplate, new Dictionary<string, string?> { ["QUESTION"] = request.Question }));
            return builder.ToString();
        }

        private string BuildKnowledgeMatchFallback(
            TicketContext? ticketContext,
            IReadOnlyCollection<KnowledgeBaseChunkMatch> knowledgeMatches,
            IReadOnlyCollection<CopilotTicketCitation> similarTickets)
        {
            var lines = new List<string>();
            if (ticketContext != null)
            {
                lines.Add(CopilotTextTemplate.Apply(
                    _text.InvestigationFallbackTicketTemplate,
                    new Dictionary<string, string?>
                    {
                        ["TICKET_NUMBER"] = ticketContext.TicketNumber,
                        ["STATUS"] = ticketContext.Status,
                        ["PRIORITY"] = ticketContext.Priority
                    }));
            }

            if (knowledgeMatches.Any())
            {
                lines.Add(CopilotTextTemplate.Apply(_text.InvestigationFallbackKnowledgeTemplate, new Dictionary<string, string?> { ["COUNT"] = knowledgeMatches.Count.ToString() }));
            }

            if (similarTickets.Any())
            {
                lines.Add(CopilotTextTemplate.Apply(_text.InvestigationFallbackSimilarTemplate, new Dictionary<string, string?> { ["COUNT"] = similarTickets.Count.ToString() }));
            }

            if (!lines.Any())
            {
                lines.Add(_text.InvestigationFallbackNoEvidence);
            }

            return string.Join(" ", lines);
        }

        private List<string> BuildFollowUpPrompts(TicketContext? ticketContext, CopilotQuestionContext questionContext)
        {
            if (ticketContext != null)
            {
                return
                [
                    CopilotTextTemplate.Apply(_text.TicketFollowUpNextActionTemplate, new Dictionary<string, string?> { ["TICKET_NUMBER"] = ticketContext.TicketNumber }),
                    CopilotTextTemplate.Apply(_text.TicketFollowUpSimilarTemplate, new Dictionary<string, string?> { ["TICKET_NUMBER"] = ticketContext.TicketNumber }),
                    CopilotTextTemplate.Apply(_text.TicketFollowUpKnowledgeTemplate, new Dictionary<string, string?> { ["TICKET_NUMBER"] = ticketContext.TicketNumber })
                ];
            }

            return
            [
                CopilotTextTemplate.Apply(_text.GeneralFollowUpSimilarTemplate, new Dictionary<string, string?> { ["QUESTION"] = questionContext.OriginalQuestion }),
                _text.GeneralFollowUpNextAction,
                _text.GeneralFollowUpKnowledge
            ];
        }
    }
}
