using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Pipeline.Abstractions;
using Microsoft.Extensions.Logging;

namespace AISupportAnalysisPlatform.Services.AI.Pipeline.Execution
{
    /// <summary>
    /// Execution step for the analytics pipeline.
    /// It uses the mature catalog executor to run the metadata-grounded plan.
    /// </summary>
    public class PipelineExecutorService : IAnalyticsStep
    {
        private readonly CopilotDataQueryExecutorService _catalogExecutor;
        private readonly ILogger<PipelineExecutorService> _logger;

        public int Order => 30; // Third step: data execution

        public PipelineExecutorService(
            CopilotDataQueryExecutorService catalogExecutor,
            ILogger<PipelineExecutorService> logger)
        {
            _catalogExecutor = catalogExecutor;
            _logger = logger;
        }

        public async Task ExecuteAsync(AnalyticsPipelineContext context)
        {
            if (context.DataIntentPlan == null || context.Entity == null || !context.IsValid) return;

            var step = new CopilotExecutionStep
            {
                Action = "Run catalog query",
                Detail = "Execute the validated metadata plan against the application database using the mature catalog executor.",
                Layer = CopilotExecutionLayer.DataExecution
            };
            context.ExecutionSteps.Add(step);

            try
            {
                var result = await _catalogExecutor.TryExecuteAsync(context.DataIntentPlan, context.CancellationToken);
                if (result == null)
                {
                    context.Fail("The mature catalog executor could not safely complete this request based on current metadata rules.");
                    return;
                }

                context.ExecutionResult = result;

                step.SubSteps.Add(new CopilotExecutionStep
                {
                    Action = "Execute catalog plan",
                    Detail = $"Verified {context.DataIntentPlan.Operation} on {context.DataIntentPlan.PrimaryEntity}. Found {result.TotalCount} records.",
                    Layer = CopilotExecutionLayer.DataExecution,
                    TechnicalData = result.GeneratedSql,
                    Status = CopilotStepStatus.Ok
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipelineExecutorService failed for {Entity}", context.Entity.Name);
                context.Fail("I encountered an error while accessing the database. Please try a different question.");
            }
        }
    }
}
