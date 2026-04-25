using System.Globalization;
using System.Text;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Pipeline.Abstractions;

namespace AISupportAnalysisPlatform.Services.AI.Pipeline.Formatting
{
    /// <summary>
    /// Formats catalog analytics results into readable answers.
    /// It keeps grouped, scalar, detail, and list responses distinct so the pipeline output is easy to inspect.
    /// </summary>
    public class PipelineFormatterService : IAnalyticsStep
    {
        public int Order => 40;

        public Task ExecuteAsync(AnalyticsPipelineContext context)
        {
            if (!context.IsValid || context.DataIntentPlan == null)
            {
                context.ExecutionResult = new AdminCopilotDynamicTicketQueryExecution
                {
                    Plan = context.Plan ?? new AdminCopilotDynamicTicketQueryPlan(),
                    Answer = context.ClarificationMessage ?? "I need more details to answer that correctly.",
                    Summary = context.ClarificationMessage ?? "Catalog clarification required.",
                    ExecutionSteps = context.ExecutionSteps
                };
                return Task.CompletedTask;
            }

            var result = context.ExecutionResult;
            if (result == null)
            {
                context.Fail("No data results were returned from the catalog execution stage.");
                return Task.CompletedTask;
            }

            // Link plan and steps
            result.Plan ??= context.Plan ?? new AdminCopilotDynamicTicketQueryPlan();
            result.ExecutionSteps = context.ExecutionSteps;

            // Refine the answer if needed
            if (string.IsNullOrWhiteSpace(result.Answer) || result.Answer.Contains("Returned"))
            {
                result.Answer = BuildAnswer(context.DataIntentPlan, context.Entity, result);
            }

            if (context.DataIntentPlan.ValidationMessages.Count > 0)
            {
                result.Answer += "\n\n⚠️ Notes: " + 
                    string.Join("; ", context.DataIntentPlan.ValidationMessages);
            }

            return Task.CompletedTask;
        }

        private static string BuildAnswer(
            CopilotDataIntentPlan plan,
            CopilotEntityDefinition? entity,
            AdminCopilotDynamicTicketQueryExecution result)
        {
            if (result.StructuredRows.Count == 0)
            {
                return "No approved data matched your request in the catalog.";
            }

            var entityName = entity?.Name ?? plan.PrimaryEntity;
            var builder = new StringBuilder();

            if (plan.Operation.Equals("count", StringComparison.OrdinalIgnoreCase))
            {
                return $"Found **{result.TotalCount}** matching {entityName.ToLowerInvariant()} records based on the approved catalog rules.";
            }

            if (plan.GroupBy.Any())
            {
                builder.AppendLine($"### Breakdown of {entityName} by {Humanize(plan.GroupBy.First())}\n");
                foreach (var row in result.StructuredRows.Take(10))
                {
                    var key = row.Values.Values.FirstOrDefault() ?? "Unknown";
                    var val = row.Values.Values.Skip(1).FirstOrDefault() ?? "0";
                    builder.AppendLine($"- **{key}**: {val}");
                }
                return builder.ToString().TrimEnd();
            }

            builder.AppendLine($"**{entityName}** results ({result.TotalCount} total)\n");
            AppendMarkdownTable(builder, result.StructuredColumns, result.StructuredRows);
            
            return builder.ToString().TrimEnd();
        }

        private static void AppendMarkdownTable(
            StringBuilder builder,
            IReadOnlyList<string> columns,
            IReadOnlyList<AdminCopilotStructuredResultRow> rows)
        {
            builder.AppendLine($"| {string.Join(" | ", columns)} |");
            builder.AppendLine($"|{string.Join("|", columns.Select(_ => "---"))}|");
            foreach (var row in rows.Take(10))
            {
                builder.AppendLine($"| {string.Join(" | ", columns.Select(column => row.Values.GetValueOrDefault(column, "-")))} |");
            }

            if (rows.Count > 10)
            {
                builder.AppendLine($"\n*Showing top 10 of {rows.Count} returned rows*");
            }
        }

        private static string Humanize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
                    sb.Append(' ');
                sb.Append(value[i]);
            }
            return sb.ToString().Replace('_', ' ').Trim();
        }
    }
}
