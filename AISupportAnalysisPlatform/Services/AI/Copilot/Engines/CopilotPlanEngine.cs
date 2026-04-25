using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using System.Text.Json;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Planning stage for the copilot.
    /// Turns the chosen intent into an executable plan: ticket scope, analytics plan, or tool payload.
    /// </summary>
    public class CopilotPlanEngine : ICopilotPlanEngine
    {
        private readonly ICopilotKnowledgeEngine _knowledge;
        private readonly ICopilotQuestionPreprocessor _preprocessor;
        private readonly CopilotRequestDecomposer _requestDecomposer;
        private readonly CopilotDataIntentPlannerService _dataIntentPlanner;
        private readonly CopilotDataCatalogService _catalogService;

        public CopilotPlanEngine(
            ICopilotKnowledgeEngine knowledge,
            ICopilotQuestionPreprocessor preprocessor,
            CopilotRequestDecomposer requestDecomposer,
            CopilotDataIntentPlannerService dataIntentPlanner,
            CopilotDataCatalogService catalogService)
        {
            _knowledge = knowledge;
            _preprocessor = preprocessor;
            _requestDecomposer = requestDecomposer;
            _dataIntentPlanner = dataIntentPlanner;
            _catalogService = catalogService;
        }

        public async Task<CopilotExecutionPlan> BuildAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotIntentDecision decision,
            CancellationToken cancellationToken = default)
        {
            var plan = new CopilotExecutionPlan
            {
                Decision = decision,
                Summary = decision.Reason,
                TicketNumber = questionContext.TicketNumber,
                RequiresClarification = decision.RequiresClarification,
                ClarificationQuestion = decision.ClarificationQuestion,
                ToolName = decision.ToolName,
                SearchText = !string.IsNullOrWhiteSpace(decision.ToolQuery)
                    ? decision.ToolQuery
                    : string.IsNullOrWhiteSpace(questionContext.SearchText) ? request.Question : questionContext.SearchText,
                ToolParameters = new Dictionary<string, string>(decision.ToolParameters, StringComparer.OrdinalIgnoreCase),
                Sources = ResolveSources(decision)
            };

            // Safety: if the intent is search-heavy but search text is missing/empty, flag for clarification.
            if (string.IsNullOrWhiteSpace(plan.SearchText) && 
                decision.Intent == CopilotIntentKind.ExternalToolQuery)
            {
                plan.RequiresClarification = true;
                plan.ClarificationQuestion = _knowledge.Messages.VerificationClarificationMessage;
            }

            var decomposition = await _requestDecomposer.DecomposeAsync(request, questionContext, decision, cancellationToken);
            if (decomposition.SubRequests.Count > 1)
            {
                plan.SubTasks.AddRange(await BuildSubTaskPlansAsync(request, questionContext, decomposition, cancellationToken));
                plan.Summary = $"Coordinating {plan.SubTasks.Count} copilot sub-requests.";
                
                var complexityStep = new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.Router,
                    Action = "Evaluating Prompt Complexity",
                    Detail = $"The request was identified as multi-part. Decomposed into {plan.SubTasks.Count} independent sub-requests for parallel processing.",
                    TechnicalData = JsonSerializer.Serialize(new
                    {
                        subrequests = decomposition.SubRequests.Select((subrequest, index) => new
                        {
                            index = index + 1,
                            text = subrequest.Text,
                            kind = subrequest.Kind.ToString(),
                            source = subrequest.Source.ToString(),
                            confidence = subrequest.Confidence.ToString(),
                            reason = subrequest.Reason,
                            toolName = subrequest.ToolName,
                            requiresClarification = subrequest.RequiresClarification,
                            clarificationQuestion = subrequest.ClarificationQuestion
                        })
                    })
                };

                // Nest sub-task planning into the complexity step
                AddSubTaskPlanningSteps(complexityStep, plan);
                plan.PlanSteps.Add(complexityStep);
                
                return plan;
            }

            return decision.Intent switch
            {
                CopilotIntentKind.DataQuery => await BuildStructuredQueryPlanAsync(plan, request, questionContext, cancellationToken),
                _ => plan
            };
        }

        private async Task<List<CopilotExecutionPlan>> BuildSubTaskPlansAsync(
            CopilotChatRequest request,
            CopilotQuestionContext parentContext,
            CopilotRequestDecomposition decomposition,
            CancellationToken cancellationToken)
        {
            var plans = new List<CopilotExecutionPlan>();
            foreach (var subRequest in decomposition.SubRequests)
            {
                var subChatRequest = new CopilotChatRequest
                {
                    Question = subRequest.Text,
                    Surface = request.Surface,
                    History = request.History,
                    ReportStartDate = request.ReportStartDate,
                    ReportEndDate = request.ReportEndDate
                };

                var decision = new CopilotIntentDecision
                {
                    Intent = subRequest.Kind,
                    PrimarySource = subRequest.Source,
                    Confidence = subRequest.Confidence,
                    Reason = subRequest.Reason,
                    ToolName = subRequest.ToolName,
                    ToolQuery = subRequest.ToolQuery,
                    ToolParameters = subRequest.ToolParameters,
                    RequiresClarification = subRequest.RequiresClarification,
                    ClarificationQuestion = subRequest.ClarificationQuestion
                };

                var subContext = _preprocessor.Process(subChatRequest, parentContext.ConversationContext);
                var subPlan = new CopilotExecutionPlan
                {
                    Decision = decision,
                    Summary = decision.Reason,
                    TicketNumber = subContext.TicketNumber,
                    RequiresClarification = decision.RequiresClarification,
                    ClarificationQuestion = decision.ClarificationQuestion,
                    ToolName = decision.ToolName,
                    SearchText = !string.IsNullOrWhiteSpace(decision.ToolQuery) ? decision.ToolQuery : subRequest.Text,
                    ToolParameters = new Dictionary<string, string>(decision.ToolParameters, StringComparer.OrdinalIgnoreCase),
                    Sources = ResolveSources(decision)
                };

                if (subRequest.Kind == CopilotIntentKind.DataQuery)
                {
                    subPlan = await BuildStructuredQueryPlanAsync(subPlan, subChatRequest, subContext, cancellationToken);
                }

                plans.Add(subPlan);
            }

            return plans;
        }

        private static void AddSubTaskPlanningSteps(CopilotExecutionStep parentStep, CopilotExecutionPlan parentPlan)
        {
            for (var i = 0; i < parentPlan.SubTasks.Count; i++)
            {
                var subTask = parentPlan.SubTasks[i];
                var subRequestStep = new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.Router,
                    Action = $"Analyzing Sub-Task {i + 1}",
                    Detail = $"Processing branch: \"{subTask.SearchText}\" [{subTask.Decision.Intent}]",
                    Status = CopilotStepStatus.Ok,
                    TechnicalData = JsonSerializer.Serialize(new
                    {
                        subrequest = i + 1,
                        intent = subTask.Decision.Intent.ToString(),
                        source = subTask.Decision.PrimarySource.ToString(),
                        searchText = subTask.SearchText,
                        toolName = subTask.ToolName
                    })
                };

                if (subTask.PlanSteps.Any())
                {
                    subRequestStep.SubSteps.AddRange(subTask.PlanSteps);
                }
                else
                {
                    subRequestStep.Detail += " - No further planning required.";
                }

                parentStep.SubSteps.Add(subRequestStep);
            }
        }

        private Task<CopilotExecutionPlan> BuildStructuredQueryPlanAsync(
            CopilotExecutionPlan plan,
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CancellationToken cancellationToken)
            => BuildStructuredQueryPlanCoreAsync(plan, request, questionContext, cancellationToken);

        private async Task<CopilotExecutionPlan> BuildStructuredQueryPlanCoreAsync(
            CopilotExecutionPlan plan,
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CancellationToken cancellationToken)
        {
            var shouldPrebuildCatalogPlan = await ShouldPrebuildCatalogPlanAsync(questionContext, cancellationToken);
            return shouldPrebuildCatalogPlan
                ? await BuildComplexStructuredQueryPlanAsync(plan, request, questionContext, cancellationToken)
                : await BuildSimpleStructuredQueryPlanAsync(plan, request, questionContext);
        }

        private Task<CopilotExecutionPlan> BuildSimpleStructuredQueryPlanAsync(
            CopilotExecutionPlan plan,
            CopilotChatRequest request,
            CopilotQuestionContext questionContext)
        {
            plan.PlanSteps.Add(new CopilotExecutionStep
            {
                Layer = CopilotExecutionLayer.Router,
                Action = "Preparing Primary Analytics Route",
                Detail = "The primary analytics pipeline will analyze the raw question directly. Detailed catalog fallback planning is deferred unless the primary route cannot produce a safe result.",
                TechnicalData = JsonSerializer.Serialize(new
                {
                    request.Question,
                    questionContext.NormalizedQuestion,
                    questionContext.HasTicketReference,
                    questionContext.LooksLikeDataQuestion,
                    questionContext.IsFollowUpQuestion,
                    DeferredFallbackPlanning = true
                })
            });

            return Task.FromResult(plan);
        }

        private async Task<CopilotExecutionPlan> BuildComplexStructuredQueryPlanAsync(
            CopilotExecutionPlan plan,
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CancellationToken cancellationToken)
        {
            plan.PlanSteps.Add(new CopilotExecutionStep
            {
                Layer = CopilotExecutionLayer.Router,
                Action = "Preparing Catalog Intent Plan",
                Detail = "This request needs richer catalog understanding, so the system is preparing the metadata-grounded data plan before execution.",
                TechnicalData = JsonSerializer.Serialize(new
                {
                    request.Question,
                    questionContext.NormalizedQuestion,
                    questionContext.HasTicketReference,
                    questionContext.IsFollowUpQuestion,
                    DeferredFallbackPlanning = false
                })
            });

            try
            {
                plan.DataIntentPlan = await _dataIntentPlanner.BuildAsync(request, questionContext, cancellationToken);
                plan.PlanSteps.Add(new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.DataPlanning,
                    Action = "Catalog Intent Plan Ready",
                    Detail = plan.DataIntentPlan.RequiresClarification
                        ? "The catalog planner needs clarification before a safe data query can run."
                        : $"Prepared {plan.DataIntentPlan.Operation} on {plan.DataIntentPlan.PrimaryEntity} using {plan.DataIntentPlan.Entities.Count} catalog entities.",
                    TechnicalData = JsonSerializer.Serialize(plan.DataIntentPlan),
                    Status = plan.DataIntentPlan.RequiresClarification ? CopilotStepStatus.Warn : CopilotStepStatus.Ok
                });
            }
            catch (Exception ex)
            {
                plan.PlanSteps.Add(new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.DataPlanning,
                    Action = "Catalog Intent Planning Failed",
                    Detail = ex.Message,
                    TechnicalData = ex.ToString(),
                    Status = CopilotStepStatus.Error
                });
            }

            return plan;
        }

        private async Task<bool> ShouldPrebuildCatalogPlanAsync(
            CopilotQuestionContext questionContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (questionContext.IsFollowUpQuestion)
            {
                return true;
            }

            var normalized = questionContext.NormalizedQuestion;
            var catalog = await _catalogService.GetCatalogAsync();
            var entityScores = catalog.Entities
                .Select(entity => new
                {
                    Entity = entity,
                    Score = ScoreEntityMatch(normalized, entity),
                    Position = FindEntityMatchPosition(normalized, entity)
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Position)
                .ToList();

            if (entityScores.Count == 0)
            {
                return false;
            }

            if (entityScores.Count > 1)
            {
                return true;
            }

            var primaryEntity = entityScores[0].Entity;
            if (HasRelationshipSignal(normalized, primaryEntity, catalog))
            {
                return true;
            }

            return HasCrossEntityFieldSignal(normalized, primaryEntity, catalog);
        }

        private static int ScoreEntityMatch(string normalized, CopilotEntityDefinition entity)
        {
            var score = 0;
            foreach (var term in EnumerateEntityTerms(entity))
            {
                if (ContainsPhrase(normalized, term))
                {
                    score += term.Length;
                }
            }

            return score;
        }

        private static int FindEntityMatchPosition(string normalized, CopilotEntityDefinition entity)
        {
            var positions = EnumerateEntityTerms(entity)
                .Select(term => normalized.IndexOf(term, StringComparison.OrdinalIgnoreCase))
                .Where(index => index >= 0)
                .DefaultIfEmpty(int.MaxValue);

            return positions.Min();
        }

        private static bool HasRelationshipSignal(
            string normalized,
            CopilotEntityDefinition primaryEntity,
            CopilotDataCatalog catalog)
        {
            return primaryEntity.Relationships.Any(relationship =>
            {
                if (ContainsPhrase(normalized, NormalizePhrase(relationship.Name)))
                {
                    return true;
                }

                var target = catalog.Entities.FirstOrDefault(entity =>
                    entity.Name.Equals(relationship.Target, StringComparison.OrdinalIgnoreCase));
                return target != null && EnumerateEntityTerms(target).Any(term => ContainsPhrase(normalized, term));
            });
        }

        private static bool HasCrossEntityFieldSignal(
            string normalized,
            CopilotEntityDefinition primaryEntity,
            CopilotDataCatalog catalog)
        {
            return catalog.Entities
                .Where(entity => !entity.Name.Equals(primaryEntity.Name, StringComparison.OrdinalIgnoreCase))
                .Any(entity => entity.Fields.Any(field =>
                    field.Aliases.Any(alias => ContainsPhrase(normalized, alias)) ||
                    ContainsPhrase(normalized, field.Name)));
        }

        private static IEnumerable<string> EnumerateEntityTerms(CopilotEntityDefinition entity)
            => entity.Aliases
                .Append(entity.Name)
                .Append(entity.Table)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(NormalizePhrase)
                .Distinct(StringComparer.OrdinalIgnoreCase);

        private static bool ContainsPhrase(string text, string phrase)
            => text.Contains($" {phrase} ", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith($"{phrase} ", StringComparison.OrdinalIgnoreCase) ||
               text.EndsWith($" {phrase}", StringComparison.OrdinalIgnoreCase) ||
               text.Equals(phrase, StringComparison.OrdinalIgnoreCase);

        private static string NormalizePhrase(string value)
            => string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        private static List<CopilotDataSource> ResolveSources(CopilotIntentDecision decision)
        {
            return decision.Intent switch
            {
                CopilotIntentKind.DataQuery => [CopilotDataSource.Database],
                CopilotIntentKind.ExternalToolQuery => [CopilotDataSource.ExternalTool],
                _ => [CopilotDataSource.None]
            };
        }
    }
}
