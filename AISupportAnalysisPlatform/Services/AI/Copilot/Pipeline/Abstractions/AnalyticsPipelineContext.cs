using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI.Pipeline.Abstractions
{
    /// <summary>
    /// Expanded shared state carried through the full Copilot execution pipeline.
    /// This context acts as the single source of truth for a single user request.
    /// </summary>
    public class AnalyticsPipelineContext
    {
        // Inputs
        public string RawQuestion { get; }
        public CancellationToken CancellationToken { get; }

        // Analysis Results (Populated by Analysis steps)
        public AdminCopilotDynamicTicketQueryPlan? Plan { get; set; }
        public CopilotDataIntentPlan? DataIntentPlan { get; set; }

        // Schema & Metadata (Populated by Analysis steps)
        public CopilotEntityDefinition? Entity { get; set; }
        public string? TargetViewName { get; set; }

        // Evaluation & Refinement (Populated by Evaluation steps)
        public bool IsValid { get; set; } = true;
        public string? ClarificationMessage { get; set; }

        // Final Response (Populated by Execution/Formatting steps)
        public AdminCopilotDynamicTicketQueryExecution? ExecutionResult { get; set; }
        public List<CopilotExecutionStep> ExecutionSteps { get; set; } = new();

        public AnalyticsPipelineContext(string rawQuestion, CancellationToken ct = default)
        {
            RawQuestion = rawQuestion;
            CancellationToken = ct;
        }

        public void Fail(string message)
        {
            IsValid = false;
            ClarificationMessage = message;
            if (Plan != null)
            {
                Plan.RequiresClarification = true;
                Plan.ClarificationQuestion = message;
            }
        }
    }
}
