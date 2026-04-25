using System.Threading.Tasks;

namespace AISupportAnalysisPlatform.Services.AI.Pipeline.Abstractions
{
    /// <summary>
    /// Defines a single step in the catalog analytics pipeline.
    /// </summary>
    public interface IAnalyticsStep
    {
        /// <summary>
        /// Execution order (lower runs first).
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Executes the step logic using the shared context.
        /// </summary>
        Task ExecuteAsync(AnalyticsPipelineContext context);
    }
}
