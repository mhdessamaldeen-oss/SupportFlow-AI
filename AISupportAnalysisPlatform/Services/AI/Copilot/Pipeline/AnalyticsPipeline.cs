using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Pipeline.Abstractions;
using Microsoft.Extensions.Logging;

namespace AISupportAnalysisPlatform.Services.AI.Pipeline
{
    /// <summary>
    /// Orchestrates the multi-step catalog analytics execution process.
    /// The pipeline is strictly ordered and carries a shared context for state management.
    /// </summary>
    public class AnalyticsPipeline
    {
        private readonly IEnumerable<IAnalyticsStep> _steps;
        private readonly ILogger<AnalyticsPipeline> _logger;

        public AnalyticsPipeline(
            IEnumerable<IAnalyticsStep> steps,
            ILogger<AnalyticsPipeline> logger)
        {
            _steps = steps.OrderBy(s => s.Order);
            _logger = logger;
        }

        public async Task<AdminCopilotDynamicTicketQueryExecution?> ExecuteAsync(string question, CancellationToken ct = default)
        {
            var context = new AnalyticsPipelineContext(question, ct);
            _logger.LogInformation("Starting catalog analytics pipeline for: {Question}", question);

            try
            {
                foreach (var step in _steps)
                {
                    if (!context.IsValid && step.Order >= 20) 
                    {
                        // Stop if invalid, but allow formatting (Order 40) to present the error.
                        if (step.Order < 40) continue;
                    }

                    await step.ExecuteAsync(context);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analytics pipeline failed unexpectedly.");
                context.Fail("I encountered an internal error while analyzing your request.");
            }

            return context.ExecutionResult;
        }
    }
}
