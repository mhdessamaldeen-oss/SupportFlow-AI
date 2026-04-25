using System.Diagnostics;
using System.Text.Json;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using AISupportAnalysisPlatform.Services.AI.Providers;
using Microsoft.Extensions.Options;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Top-level copilot orchestrator.
    /// Reads one user question, builds context, routes it, plans it, executes it,
    /// verifies the result, and stores the trace for later inspection.
    /// </summary>
    public class CopilotChatEngine : ICopilotChatEngine
    {
        private readonly ICopilotConversationContextService _conversationContextService;
        private readonly ICopilotIntelligenceEngine _intelligence;
        private readonly ICopilotPlanEngine _planEngine;
        private readonly ICopilotExecutionEngine _executionService;
        private readonly ICopilotVerificationEngine _verificationService;
        private readonly CopilotTraceHistoryService _traceHistoryService;
        private readonly IAiProviderFactory _providerFactory;
        private readonly ICopilotKnowledgeEngine _knowledge;
        private readonly ICopilotGroundingService _grounding;
        private readonly IOptionsMonitor<CopilotTextSettings> _settingsMonitor;
        private readonly ILogger<CopilotChatEngine> _logger;

        public CopilotChatEngine(
            ICopilotConversationContextService conversationContextService,
            ICopilotIntelligenceEngine intelligence,
            ICopilotPlanEngine planEngine,
            ICopilotExecutionEngine executionService,
            ICopilotVerificationEngine verificationService,
            CopilotTraceHistoryService traceHistoryService,
            IAiProviderFactory providerFactory,
            ICopilotKnowledgeEngine knowledge,
            ICopilotGroundingService grounding,
            IOptionsMonitor<CopilotTextSettings> settingsMonitor,
            ILogger<CopilotChatEngine> logger)
        {
            _conversationContextService = conversationContextService;
            _intelligence = intelligence;
            _planEngine = planEngine;
            _executionService = executionService;
            _verificationService = verificationService;
            _traceHistoryService = traceHistoryService;
            _providerFactory = providerFactory;
            _knowledge = knowledge;
            _grounding = grounding;
            _settingsMonitor = settingsMonitor;
            _logger = logger;
        }

        public async Task<CopilotChatResponse> AskAsync(CopilotChatRequest request, CancellationToken cancellationToken = default)
        {
            var totalStopwatch = Stopwatch.StartNew();
            var response = CreateResponse(request);
            var resolvedIntent = "Unknown";

            if (string.IsNullOrWhiteSpace(request.Question))
            {
                ApplyEmptyQuestion(response);
                return response;
            }

            // Create a linked token that enforces the configurable pipeline timeout.
            // If the caller already has a token (e.g. HTTP request abort), both signals are respected.
            var timeoutSeconds = _settingsMonitor.CurrentValue.Pipeline.PipelineTimeoutSeconds;
            using var timeoutCts = timeoutSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (timeoutCts != null) timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var pipelineToken = timeoutCts?.Token ?? cancellationToken;

            try
            {
                var provider = _providerFactory.GetActiveProvider();

                // Stage 1: initialize the response envelope and record which model is serving this run.
                StartPipeline(response, provider.ModelName);
                _logger.LogInformation("Copilot pipeline started. Question={Question}, Surface={Surface}", request.Question, request.Surface);

                // Stage 2: load prior conversation state.
                // This gives the pipeline the recent trace, previous query plan, and last ticket reference
                // so short follow-up prompts can inherit the right context.
                pipelineToken.ThrowIfCancellationRequested();
                var conversationContext = await _conversationContextService.BuildAsync(request, pipelineToken);

                // Stage 3: The Intelligence Pillar (Preprocessing + Classification + Fuzzy Logic).
                // Sub-steps:
                // 1. normalize and preprocess the question
                // 2. apply cheap heuristic/fuzzy scoring
                // 3. fall back to the classifier when rules are not strong enough
                pipelineToken.ThrowIfCancellationRequested();
                var intelligence = await _intelligence.AnalyzeAsync(request, conversationContext, pipelineToken);
                
                var questionContext = intelligence.QuestionContext;
                var decision = intelligence.IntentDecision;
                resolvedIntent = decision.Intent.ToString();

                ApplyIntelligenceTraces(response, intelligence);
                
                _logger.LogInformation("Intelligence completed. Intent={Intent}, Confidence={Confidence}, RuleScore={Score:P0}, IsFallback={IsFallback}",
                    decision.Intent, decision.Confidence, intelligence.RuleConfidenceScore, decision.IsFallback);

                // Stage 4: turn the routing decision into an executable plan with any resolved ticket, tool, or query details.
                // This is where the copilot decides the exact ticket id, analytics filters, sub-queries,
                // or tool parameters it will run.
                pipelineToken.ThrowIfCancellationRequested();
                var plan = await BuildPlanAsync(request, questionContext, decision, response, pipelineToken);

                // Stage 5: execute the selected route and copy the result payload onto the response.
                // The executor returns the answer plus structured payloads like tables, rows, citations, and SQL trace.
                pipelineToken.ThrowIfCancellationRequested();
                var execution = await ExecutePlanAsync(request, questionContext, decision, plan, response, pipelineToken);

                // Stage 6: verify that the result matches the chosen route and downgrade to clarification if needed.
                // This is the last safety check before the response goes back to the UI.
                ApplyVerification(plan, execution, response);

                // Stage 7: AI Grounding Audit (Forensic Hallucination Check).
                // We ask the AI auditor to check the final answer against the retrieved evidence.
                pipelineToken.ThrowIfCancellationRequested();
                var groundingResult = ShouldSkipGroundingAudit(decision, questionContext, execution)
                    ? new CopilotGroundingResult
                    {
                        IsGrounded = true,
                        Confidence = 0.95,
                        Analysis = "Skipped grounding audit because this was a local greeting/capability response."
                    }
                    : await _grounding.VerifyAsync(
                        request.Question,
                        response.Answer,
                        execution,
                        pipelineToken);

                ApplyGroundingResult(response, groundingResult);
            }
            catch (OperationCanceledException) when (timeoutCts != null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The pipeline-level timeout fired, not the caller's token.
                _logger.LogWarning("Copilot pipeline timed out after {TimeoutSeconds}s. Question={Question}", timeoutSeconds, request.Question);
                ApplyGracefulDegradation(response, _knowledge.Messages.PipelineTimeoutMessage);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Copilot pipeline cancelled. Question={Question}", request.Question);
                ApplyGracefulDegradation(response, _knowledge.Messages.PipelineCancelledMessage);
            }
            catch (AggregateException aggEx)
            {
                var flat = aggEx.Flatten();
                _logger.LogError(aggEx, "Copilot pipeline failed during parallel execution. Errors: {Messages}", 
                    string.Join("; ", flat.InnerExceptions.Select(e => e.Message)));
                
                ApplyGracefulDegradation(response, _knowledge.Messages.PipelineErrorMessage);
                response.ExecutionDetails.AddStep(
                    CopilotExecutionLayer.Complete,
                    _knowledge.Messages.PipelineErrorStepAction,
                    flat.InnerExceptions.FirstOrDefault()?.Message ?? aggEx.Message,
                    CopilotStepStatus.Error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Copilot pipeline failed. Question={Question}, ResolvedIntent={Intent}", request.Question, resolvedIntent);
                ApplyGracefulDegradation(response, _knowledge.Messages.PipelineErrorMessage);
                response.ExecutionDetails.AddStep(
                    CopilotExecutionLayer.Complete,
                    _knowledge.Messages.PipelineErrorStepAction,
                    ex.Message,
                    CopilotStepStatus.Error);
            }

            totalStopwatch.Stop();

            // Stage 7: finalize timing and persist the trace so the run can be inspected later.
            // Trace persistence must never block the user response, so failures here are only logged.
            _logger.LogInformation("Copilot pipeline completed. Intent={Intent}, ElapsedMs={ElapsedMs}, EvidenceStrength={Strength}",
                resolvedIntent, totalStopwatch.ElapsedMilliseconds, response.EvidenceStrength);

            await FinalizeResponseAsync(response, resolvedIntent, totalStopwatch.ElapsedMilliseconds);
            return response;
        }

        private static CopilotChatResponse CreateResponse(CopilotChatRequest request)
        {
            return new CopilotChatResponse
            {
                Question = request.Question
            };
        }

        private void ApplyEmptyQuestion(CopilotChatResponse response)
        {
            response.Notes = _knowledge.Messages.EmptyQuestionMessage;
            response.Answer = _knowledge.Messages.EmptyQuestionMessage;
        }

        private void ApplyGracefulDegradation(CopilotChatResponse response, string message)
        {
            // Always overwrite: a partial answer produced before the pipeline failed is unreliable
            // and must not be shown to the user as if it were a complete, correct response.
            response.Answer = message;
            response.ResponseMode = ResponseMode.Conversational;
            response.EvidenceStrength = EvidenceStrength.Weak;
            response.Notes = message;
        }

        private void StartPipeline(CopilotChatResponse response, string modelName)
        {
            // Record the model and add the first execution-story step before any classification or planning begins.
            response.ModelName = modelName;
            response.ExecutionDetails.AddStep(
                CopilotExecutionLayer.Context,
                "Pipeline Initialization [CopilotChatEngine -> StartPipeline]",
                $"Pipeline initialized with {modelName}. Stage: Context Gathering.",
                CopilotStepStatus.Ok);
        }

        private void ApplyIntelligenceTraces(CopilotChatResponse response, CopilotIntelligenceContext intelligence)
        {
            var ctx = intelligence.QuestionContext;
            var decision = intelligence.IntentDecision;

            // Create a parent step for the Intelligence Pillar
            var pillarStep = new CopilotExecutionStep
            {
                Layer = CopilotExecutionLayer.Router,
                Action = "Intelligence Pillar [IntelligenceEngine -> Analyze]",
                Detail = $"Intent: {decision.Intent} • Confidence: {decision.Confidence} • {decision.Reason}",
                Status = decision.Confidence == RoutingConfidence.Low ? CopilotStepStatus.Warn : CopilotStepStatus.Ok
            };

            // --- Stage 3.1: Normalization ---
            var normalizationTrace = ctx.PreprocessingTrace.Where(t => t.StartsWith("Synonym") || t.StartsWith("Contextual")).ToList();
            pillarStep.AddSubStep(
                CopilotExecutionLayer.Context,
                "Input Normalization [CopilotQuestionPreprocessor -> Normalize]",
                $"Input: \"{ctx.OriginalQuestion}\"\nOutput: \"{ctx.NormalizedQuestion}\"\nDetails:\n{string.Join("\n", normalizationTrace)}",
                CopilotStepStatus.Ok,
                0,
                JsonSerializer.Serialize(new { Language = ctx.Language, Ticket = ctx.TicketNumber }));

            // --- Stage 3.2: Signal Discovery ---
            var signalTrace = ctx.PreprocessingTrace.Where(t => t.StartsWith("Signal:")).ToList();
            if (signalTrace.Any())
            {
                pillarStep.AddSubStep(
                    CopilotExecutionLayer.Context,
                    "Signal Discovery [CopilotQuestionPreprocessor -> DiscoverSignals]",
                    $"Scanning normalized input for domain indicators...\n{string.Join("\n", signalTrace)}");
            }

            // --- Stage 3.3: Heuristic Assessment ---
            var heuristicTrace = ctx.PreprocessingTrace.Where(t => t.StartsWith("Heuristics:") || t.StartsWith("Security:")).ToList();
            pillarStep.AddSubStep(
                CopilotExecutionLayer.Context,
                "Heuristic Assessment [CopilotQuestionPreprocessor -> ApplyHeuristics]",
                $"Determining intent via weighted signals...\n{string.Join("\n", heuristicTrace)}",
                ctx.IsPotentiallyUnsafe ? CopilotStepStatus.Error : CopilotStepStatus.Ok);

            // --- Stage 3.4: Context Loading ---
            if (ctx.ConversationContext.HasPriorContext)
            {
                pillarStep.AddSubStep(
                    CopilotExecutionLayer.Context,
                    "Load Conversation [CopilotConversationContextService -> BuildAsync]",
                    $"Context: {ctx.ConversationContext.Summary}");
            }

            // Update global state
            response.UsedTool = decision.ToolName;
            response.ExecutionDetails.DetectedIntent = decision.Intent.ToString();
            response.ExecutionDetails.RouteReason = decision.Reason;
            response.ExecutionDetails.PlannerConfidence = decision.Confidence;

            response.ExecutionDetails.Steps.Add(pillarStep);
        }

        private async Task<CopilotExecutionPlan> BuildPlanAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotIntentDecision decision,
            CopilotChatResponse response,
            CancellationToken cancellationToken)
        {
            // Expand the route into concrete execution data.
            // Typical outputs here are:
            // - resolved ticket id for ticket lookups
            // - dynamic analytics plan for DB questions
            // - split sub-plans for multipart analytics
            // - tool name and tool parameters for external tools
            var stepWatch = Stopwatch.StartNew();
            var plan = await _planEngine.BuildAsync(request, questionContext, decision, cancellationToken);
            stepWatch.Stop();

            // Merge granular internal planning steps into the response trace.
            if (plan.PlanSteps.Any())
            {
                response.ExecutionDetails.Steps.AddRange(plan.PlanSteps);
            }

            AddPlanStep(response, plan, stepWatch.ElapsedMilliseconds);
            return plan;
        }

        private async Task<CopilotExecutionResult> ExecutePlanAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotIntentDecision decision,
            CopilotExecutionPlan plan,
            CopilotChatResponse response,
            CancellationToken cancellationToken)
        {
            // Run the selected path and attach both the answer and technical execution details to the response.
            // This is the point where the system actually touches DB views, tools, KB search, or the general model.
            var stepWatch = Stopwatch.StartNew();
            var execution = await _executionService.ExecuteAsync(request, questionContext, plan, cancellationToken);
            stepWatch.Stop();

            // Merge granular internal execution steps into the response trace.
            if (execution.ExecutionSteps.Any())
            {
                response.ExecutionDetails.Steps.AddRange(execution.ExecutionSteps);
            }

            response.ApplyResult(execution);
            response.ExecutionDetails.AddStep(
                CopilotExecutionLayer.Executor,
                $"Execute Plan [{decision.Intent}] [CopilotExecutionEngine -> ExecuteAsync]",
                $"Input: Execution Plan\nOutput: {execution.Summary}",
                execution.EvidenceStrength == EvidenceStrength.Weak ? CopilotStepStatus.Warn : CopilotStepStatus.Ok,
                stepWatch.ElapsedMilliseconds,
                execution.TechnicalData);

            return execution;
        }

        private void ApplyVerification(
            CopilotExecutionPlan plan,
            CopilotExecutionResult execution,
            CopilotChatResponse response)
        {
            // Check that the result is coherent for the chosen route.
            // If the result looks unsafe or incomplete, switch the answer into clarification mode instead of bluffing.
            var stepWatch = Stopwatch.StartNew();
            var verification = _verificationService.Verify(plan, execution);
            stepWatch.Stop();

            if (verification.Status != CopilotVerificationStatus.Passed)
            {
                response.Notes = verification.Message;
                if (verification.Status == CopilotVerificationStatus.NeedsClarification)
                {
                    response.Answer = verification.Message;
                    response.ResponseMode = ResponseMode.Conversational;
                }
            }

            response.EvidenceStrength = verification.EvidenceStrength == EvidenceStrength.Weak
                ? response.EvidenceStrength
                : verification.EvidenceStrength;
            response.ExecutionDetails.AddStep(
                CopilotExecutionLayer.Complete,
                "Verify Response [CopilotVerificationEngine -> Verify]",
                verification.Message,
                verification.Status == CopilotVerificationStatus.Passed ? CopilotStepStatus.Ok : CopilotStepStatus.Warn,
                stepWatch.ElapsedMilliseconds);
        }

        private void ApplyGroundingResult(CopilotChatResponse response, CopilotGroundingResult grounding)
        {
            response.GroundingScore = grounding.Confidence;
            response.GroundingNotes = grounding.Analysis;

            var status = grounding.IsGrounded ? CopilotStepStatus.Ok : CopilotStepStatus.Error;
            var risks = grounding.HallucinationRisks.Any() 
                ? "\nRisks: " + string.Join("; ", grounding.HallucinationRisks) 
                : "";

            response.ExecutionDetails.AddStep(
                CopilotExecutionLayer.Complete,
                "AI Grounding Audit [CopilotGroundingService -> VerifyAsync]",
                $"Score: {grounding.Confidence:P0}. {grounding.Analysis}{risks}",
                status,
                0,
                grounding.EvidenceUsed);

            if (!grounding.IsGrounded && grounding.Confidence < 0.3)
            {
                // Aggressive downgrade: if the auditor is very sure of a hallucination, 
                // we mark the evidence as weak even if data was returned.
                response.EvidenceStrength = EvidenceStrength.Weak;
                _logger.LogWarning("Grounding failed with high confidence. Potential hallucination detected.");
            }
        }

        private static bool ShouldSkipGroundingAudit(
            CopilotIntentDecision decision,
            CopilotQuestionContext questionContext,
            CopilotExecutionResult execution)
        {
            return decision.Intent == CopilotIntentKind.GeneralChat &&
                   questionContext.LooksLikeGreeting &&
                   execution.StructuredResult == null &&
                   execution.KnowledgeMatches.Count == 0 &&
                   execution.SubResults.Count == 0 &&
                   string.IsNullOrWhiteSpace(execution.TechnicalData);
        }

        private async Task FinalizeResponseAsync(
            CopilotChatResponse response,
            string resolvedIntent,
            long elapsedMs)
        {
            // Store final timing and trace metadata after all routing, execution, and verification work has finished.
            // This makes the run available to the execution-story UI and to future follow-up prompts.
            response.ExecutionDetails.TotalElapsedMs = elapsedMs;
            try
            {
                response.TraceId = await _traceHistoryService.SaveAsync(response, elapsedMs, resolvedIntent);
            }
            catch (Exception ex)
            {
                // Never let trace persistence failure kill the user response.
                _logger.LogWarning(ex, "Failed to persist copilot trace history.");
            }
        }

        private void AddPlanStep(CopilotChatResponse response, CopilotExecutionPlan plan, long elapsedMs)
        {
            // Summarize the built plan in the execution story so the user can see what the copilot decided to run
            // before the executor touches any real data source.
            var inputLabel = $"Intent: {plan.Decision.Intent}";
            var outputLabel = plan.RequiresClarification
                ? $"Output: Clarification Needed (Question: {plan.ClarificationQuestion})"
                : $"Output: Strategy built for {string.Join(", ", plan.Sources)}";

            var technicalData = JsonSerializer.Serialize(new
            {
                intent = plan.Decision.Intent.ToString(),
                sources = plan.Sources.Select(source => source.ToString()).ToList(),
                requiresClarification = plan.RequiresClarification,
                clarificationQuestion = plan.ClarificationQuestion,
                dataIntent = plan.DataIntentPlan,
                ticketQueryPlan = plan.TicketQueryPlan == null
                    ? null
                    : new
                    {
                        targetView = plan.TicketQueryPlan.TargetView,
                        intent = plan.TicketQueryPlan.Intent.ToString(),
                        summary = plan.TicketQueryPlan.Summary,
                        maxResults = plan.TicketQueryPlan.MaxResults,
                        sortBy = plan.TicketQueryPlan.SortBy,
                        sortDirection = plan.TicketQueryPlan.SortDirection.ToString(),
                        selectedColumns = plan.TicketQueryPlan.SelectedColumns,
                        groupByField = plan.TicketQueryPlan.GroupByField,
                        aggregationType = plan.TicketQueryPlan.AggregationType,
                        aggregationColumn = plan.TicketQueryPlan.AggregationColumn
                    }
            });

            response.ExecutionDetails.AddStep(
                CopilotExecutionLayer.Router,
                "Build Execution Plan [CopilotPlanEngine -> BuildAsync]",
                $"Input: {inputLabel}\n{outputLabel}",
                plan.RequiresClarification ? CopilotStepStatus.Warn : CopilotStepStatus.Ok,
                elapsedMs,
                technicalData);
        }

    }
}
