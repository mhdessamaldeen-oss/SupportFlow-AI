using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using AISupportAnalysisPlatform.Services.AI.Pipeline;
using AISupportAnalysisPlatform.Services.AI.Providers;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Execution stage for the copilot.
    /// Dispatches the prepared plan to the correct executor and converts the result into UI-ready payloads.
    /// </summary>
    public class CopilotExecutionEngine : ICopilotExecutionEngine
    {
        private readonly CopilotInvestigationExecutor _investigationExecutor;
        private readonly CopilotExternalToolExecutor _externalToolExecutor;
        private readonly CopilotToolRegistryService _toolRegistry;
        private readonly IAiProviderFactory _providerFactory;
        private readonly ICopilotKnowledgeEngine _knowledge;
        private readonly CopilotDataQueryExecutorService _catalogDataExecutor;
        private readonly CopilotDataIntentPlannerService _dataIntentPlanner;
        private readonly AnalyticsPipeline _pipeline;
        private readonly ILogger<CopilotExecutionEngine> _logger;

        public CopilotExecutionEngine(
            CopilotInvestigationExecutor investigationExecutor,
            CopilotExternalToolExecutor externalToolExecutor,
            CopilotToolRegistryService toolRegistry,
            IAiProviderFactory providerFactory,
            ICopilotKnowledgeEngine knowledge,
            CopilotDataQueryExecutorService catalogDataExecutor,
            CopilotDataIntentPlannerService dataIntentPlanner,
            AnalyticsPipeline pipeline,
            ILogger<CopilotExecutionEngine> logger)
        {
            _investigationExecutor = investigationExecutor;
            _externalToolExecutor = externalToolExecutor;
            _toolRegistry = toolRegistry;
            _providerFactory = providerFactory;
            _knowledge = knowledge;
            _catalogDataExecutor = catalogDataExecutor;
            _dataIntentPlanner = dataIntentPlanner;
            _pipeline = pipeline;
            _logger = logger;
        }

        public async Task<CopilotExecutionResult> ExecuteAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotExecutionPlan plan,
            CancellationToken cancellationToken = default)
        {
            // If planning asked for clarification, stop here and return that question to the user.
            if (plan.RequiresClarification)
            {
                return new CopilotExecutionResult
                {
                    Answer = plan.ClarificationQuestion,
                    Notes = _knowledge.Messages.ClarificationRequiredNotes,
                    ResponseMode = ResponseMode.Conversational,
                    EvidenceStrength = EvidenceStrength.Weak,
                    Summary = plan.ClarificationQuestion
                };
            }

            // High-Integrity Refactor: Recursive Multi-Domain Execution
            if (plan.SubTasks.Any())
            {
                var tasks = plan.SubTasks.Select(async subTask =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return await ExecuteAsync(request, questionContext, subTask, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        return new CopilotExecutionResult
                        {
                            Answer = $"[Error executing {subTask.Decision.Intent} branch]: {ex.Message}",
                            Summary = $"Execution failure in {subTask.Decision.Intent}",
                            ResponseMode = ResponseMode.Conversational,
                            EvidenceStrength = EvidenceStrength.Weak,
                            ExecutionSteps = new List<CopilotExecutionStep>
                            {
                                new CopilotExecutionStep
                                {
                                    Layer = CopilotExecutionLayer.Executor,
                                    Action = $"Domain Execution Failure: {subTask.Decision.Intent}",
                                    Detail = $"An error occurred while processing the {subTask.Decision.Intent} branch of this request.",
                                    TechnicalData = ex.ToString(),
                                    Status = CopilotStepStatus.Error
                                }
                            }
                        };
                    }
                });

                var subResults = (await Task.WhenAll(tasks)).ToList();
                var combined = BuildMultiDomainResult(subResults, plan);
                combined.Answer = BuildCombinedAnswer(request.Question, subResults);
                return combined;
            }

            return plan.Decision.Intent switch
            {
                CopilotIntentKind.DataQuery => await ExecuteStructuredQueryAsync(request, questionContext, plan, cancellationToken),
                CopilotIntentKind.ExternalToolQuery => await _externalToolExecutor.ExecuteAsync(request, plan, cancellationToken),
                CopilotIntentKind.Unsupported => BuildUnsupportedResult(),
                _ => await ExecuteGeneralChatAsync(request, questionContext, plan, cancellationToken)
            };
        }

        private async Task<CopilotExecutionResult> ExecuteStructuredQueryAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotExecutionPlan plan,
            CancellationToken cancellationToken)
        {
            // ── UNIFIED PATH: Metadata-Driven Analytics Pipeline ──
            // The Pipeline now handles analysis (AI planning), evaluation (catalog vetting),
            // execution (Engine B mature SQL), and formatting (humanized responses).
            try
            {
                var pipelineResult = await _pipeline.ExecuteAsync(request.Question, cancellationToken);
                if (pipelineResult != null && !string.IsNullOrWhiteSpace(pipelineResult.Answer))
                {
                    // If the pipeline returned a clarification request, treat it as such.
                    if (pipelineResult.Plan?.RequiresClarification == true)
                    {
                        return new CopilotExecutionResult
                        {
                            Answer = pipelineResult.Answer,
                            Summary = pipelineResult.Summary ?? "Clarification required.",
                            ResponseMode = ResponseMode.Conversational,
                            EvidenceStrength = EvidenceStrength.Weak,
                            DynamicQueryPlan = pipelineResult.Plan,
                            ExecutionSteps = pipelineResult.ExecutionSteps ?? []
                        };
                    }

                    return new CopilotExecutionResult
                    {
                        Answer = pipelineResult.Answer,
                        Summary = pipelineResult.Summary ?? "Catalog query completed.",
                        ResultCount = pipelineResult.TotalCount,
                        TechnicalData = pipelineResult.GeneratedSql,
                        ResponseMode = pipelineResult.Plan?.Intent == DynamicQueryIntent.Count
                            ? ResponseMode.MetricSummary
                            : ResponseMode.StructuredTable,
                        EvidenceStrength = EvidenceStrength.High,
                        DynamicQueryPlan = pipelineResult.Plan,
                        StructuredResult = pipelineResult,
                        StructuredColumns = pipelineResult.StructuredColumns,
                        StructuredRows = pipelineResult.StructuredRows,
                        ExecutionSteps = pipelineResult.ExecutionSteps ?? []
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The unified analytics pipeline failed for '{Question}'.", request.Question);
            }

            // High-Integrity Fallback: Only used if the entire pipeline architecture fails.
            var dataIntentPlan = await EnsureFallbackDataIntentPlanAsync(request, questionContext, plan, cancellationToken);
            var catalogExecution = dataIntentPlan == null ? null : await _catalogDataExecutor.TryExecuteAsync(dataIntentPlan, cancellationToken);

            if (catalogExecution != null)
            {
                return new CopilotExecutionResult
                {
                    Answer = catalogExecution.Answer,
                    Summary = catalogExecution.Summary,
                    ResultCount = catalogExecution.TotalCount,
                    TechnicalData = catalogExecution.GeneratedSql,
                    ResponseMode = catalogExecution.Plan.Intent == DynamicQueryIntent.Count ? ResponseMode.MetricSummary : ResponseMode.StructuredTable,
                    EvidenceStrength = EvidenceStrength.High,
                    DynamicQueryPlan = catalogExecution.Plan,
                    StructuredResult = catalogExecution,
                    StructuredColumns = catalogExecution.StructuredColumns,
                    StructuredRows = catalogExecution.StructuredRows,
                    ExecutionSteps = plan.PlanSteps
                };
            }

            return new CopilotExecutionResult
            {
                Answer = _knowledge.Messages.VerificationClarificationMessage,
                Summary = "The request could not be processed by the metadata engine.",
                ResponseMode = ResponseMode.Conversational,
                EvidenceStrength = EvidenceStrength.Weak,
                ExecutionSteps = plan.PlanSteps
            };
        }

        private async Task<CopilotDataIntentPlan?> EnsureFallbackDataIntentPlanAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotExecutionPlan plan,
            CancellationToken cancellationToken)
        {
            if (plan.DataIntentPlan != null)
            {
                return plan.DataIntentPlan;
            }

            plan.PlanSteps.Add(new CopilotExecutionStep
            {
                Layer = CopilotExecutionLayer.DataPlanning,
                Action = "Preparing Catalog Fallback Plan",
                Detail = "The primary analytics route did not return a usable result, so the system is building a detailed catalog fallback plan only now.",
                Status = CopilotStepStatus.Warn
            });

            try
            {
                plan.DataIntentPlan = await _dataIntentPlanner.BuildAsync(request, questionContext, cancellationToken);
                plan.PlanSteps.Add(new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.DataPlanning,
                    Action = "Catalog Fallback Plan Ready",
                    Detail = $"Prepared {plan.DataIntentPlan.Operation} on {plan.DataIntentPlan.PrimaryEntity} for the mature catalog executor.",
                    TechnicalData = System.Text.Json.JsonSerializer.Serialize(plan.DataIntentPlan),
                    Status = plan.DataIntentPlan.RequiresClarification ? CopilotStepStatus.Warn : CopilotStepStatus.Ok
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catalog fallback data planning failed for '{Question}'.", request.Question);
                plan.PlanSteps.Add(new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.DataPlanning,
                    Action = "Catalog Fallback Planning Failed",
                    Detail = ex.Message,
                    TechnicalData = ex.ToString(),
                    Status = CopilotStepStatus.Error
                });
            }

            return plan.DataIntentPlan;
        }


        private async Task<CopilotExecutionResult> ExecuteGeneralChatAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotExecutionPlan plan,
            CancellationToken cancellationToken)
        {
            if (questionContext.LooksLikeGreeting)
            {
                return new CopilotExecutionResult
                {
                    Answer = _knowledge.Messages.GeneralChatFallbackAnswer,
                    ResponseMode = ResponseMode.Conversational,
                    EvidenceStrength = EvidenceStrength.General,
                    Summary = "Handled greeting/capability prompt locally.",
                    SuggestedPrompts = _knowledge.Messages.DefaultSuggestedPrompts.ToList(),
                    ExecutionSteps =
                    [
                        new CopilotExecutionStep
                        {
                            Layer = CopilotExecutionLayer.Executor,
                            Action = "Serve greeting locally",
                            Detail = "Detected a greeting or capability question and returned the configured copilot introduction without invoking the model.",
                            Status = CopilotStepStatus.Ok
                        }
                    ]
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            var provider = _providerFactory.GetActiveProvider();
            var tools = await _toolRegistry.GetEnabledToolsAsync();
            var toolList = tools.Any() ? string.Join(", ", tools.Select(t => t.Title)) : _knowledge.Messages.GeneralChatNoToolsText;

            var prompt = CopilotTextTemplate.Apply(
                CopilotTextTemplate.JoinLines(_knowledge.Prompts.GeneralChatPromptLines),
                new Dictionary<string, string?>
                {
                    ["TOOL_LIST"] = toolList,
                    ["CONTEXT"] = questionContext.ConversationSummary,
                    ["QUESTION"] = request.Question
                });

            var result = await provider.GenerateAsync(prompt);
            return new CopilotExecutionResult
            {
                Answer = result.Success && !string.IsNullOrWhiteSpace(result.ResponseText)
                    ? result.ResponseText.Trim()
                    : _knowledge.Messages.GeneralChatFallbackAnswer,
                ResponseMode = ResponseMode.Conversational,
                EvidenceStrength = EvidenceStrength.General,
                Summary = plan.Decision.Reason,
                SuggestedPrompts = _knowledge.Messages.DefaultSuggestedPrompts.ToList()
            };
        }

        private static string BuildCombinedAnswer(string originalQuestion, List<CopilotExecutionResult> subResults)
        {
            if (subResults.Count <= 1)
            {
                return subResults.FirstOrDefault()?.Answer ?? string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"I handled multiple parts of your request for: **{originalQuestion}**");

            for (var index = 0; index < subResults.Count; index++)
            {
                var subResult = subResults[index];
                builder.AppendLine();
                builder.AppendLine($"### {ResolveResultSectionTitle(subResult, index + 1)}");
                builder.AppendLine(subResult.Answer.Trim());
            }

            return builder.ToString().TrimEnd();
        }

        private CopilotExecutionResult BuildMultiDomainResult(List<CopilotExecutionResult> subResults, CopilotExecutionPlan plan)
        {
            var combinedAnswer = string.Join("\n\n", subResults.Select(r => r.Answer));
            var combinedSummary = $"Combined {subResults.Count} domain results: {string.Join(", ", subResults.Select(r => r.Summary))}";
            
            var technicalData = string.Join("\n\n", subResults
                .Where(r => !string.IsNullOrWhiteSpace(r.TechnicalData))
                .Select(r => $"--- Sub-Task ---\n{r.TechnicalData}"));

            // Create hierarchical execution steps
            var executionSteps = new List<CopilotExecutionStep>();
            for (int i = 0; i < subResults.Count; i++)
            {
                var subResult = subResults[i];
                var domainStep = new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.Executor,
                    Action = $"Domain Execution: {subResult.Summary}",
                    Detail = $"Result: {subResult.Answer.Take(100)}...",
                    Status = subResult.EvidenceStrength == EvidenceStrength.Weak ? CopilotStepStatus.Warn : CopilotStepStatus.Ok,
                    TechnicalData = subResult.TechnicalData,
                    SubSteps = subResult.ExecutionSteps
                };
                executionSteps.Add(domainStep);
            }

            executionSteps.Add(new CopilotExecutionStep
            {
                Layer = CopilotExecutionLayer.Executor,
                Action = "Merge branch outputs",
                Detail = "Combined each branch into source-labeled sections so unrelated domains stay separate and do not get blended together.",
                Status = CopilotStepStatus.Ok
            });

            return new CopilotExecutionResult
            {
                Answer = combinedAnswer,
                Summary = combinedSummary,
                TechnicalData = technicalData,
                ResponseMode = ResponseMode.Conversational,
                EvidenceStrength = subResults.Min(r => r.EvidenceStrength),
                ExecutionSteps = executionSteps,
                SubResults = subResults,
                StructuredResult = subResults.FirstOrDefault(r => r.StructuredResult != null)?.StructuredResult,
                KnowledgeMatches = subResults.SelectMany(r => r.KnowledgeMatches).ToList(),
                ResultCount = subResults.Sum(r => r.ResultCount),
                IsDeterministicEvidenceAnswer = subResults.All(IsDirectEvidenceResult)
            };
        }

        private static bool IsDirectEvidenceResult(CopilotExecutionResult result)
        {
            return result.StructuredResult != null ||
                result.IsDeterministicEvidenceAnswer ||
                (result.KnowledgeMatches.Count > 0 && result.SubResults.Count == 0);
        }

        private static string ResolveResultSectionTitle(CopilotExecutionResult result, int sectionIndex)
        {
            if (result.StructuredResult != null)
            {
                var target = result.StructuredResult.Plan.TargetView;
                return string.IsNullOrWhiteSpace(target)
                    ? $"Data Result {sectionIndex}"
                    : $"{target} Data Result";
            }

            if (!string.IsNullOrWhiteSpace(result.UsedTool) && !result.UsedTool.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return $"External Tool Result: {result.UsedTool}";
            }

            if (result.KnowledgeMatches.Count > 0)
            {
                return $"Knowledge Result {sectionIndex}";
            }

            return $"Result {sectionIndex}";
        }

        private CopilotExecutionResult BuildUnsupportedResult() => new()
        {
            Answer = _knowledge.Messages.UnsupportedAnswer,
            ResponseMode = ResponseMode.Conversational,
            EvidenceStrength = EvidenceStrength.Weak,
            Summary = _knowledge.Messages.UnsupportedSummary
        };

    }
}
