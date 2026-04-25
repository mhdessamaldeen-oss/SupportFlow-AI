using System.Text.Json;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Pipeline.Abstractions;

namespace AISupportAnalysisPlatform.Services.AI.Pipeline.Evaluation
{
    /// <summary>
    /// Evaluation step for the analytics pipeline.
    /// It verifies the intent plan against the catalog schema and permissions.
    /// </summary>
    public class PipelineEvaluatorService : IAnalyticsStep
    {
        private readonly CopilotDataCatalogService _catalog;

        public int Order => 20;

        public PipelineEvaluatorService(CopilotDataCatalogService catalog)
        {
            _catalog = catalog;
        }

        public async Task ExecuteAsync(AnalyticsPipelineContext context)
        {
            if (context.DataIntentPlan == null || !context.IsValid)
            {
                return;
            }

            var step = new CopilotExecutionStep
            {
                Action = "Validate catalog data plan",
                Detail = "Verify the metadata-grounded data intent plan against the approved catalog schema and capabilities.",
                Layer = CopilotExecutionLayer.DataPlanning
            };
            context.ExecutionSteps.Add(step);

            var catalog = await _catalog.GetCatalogAsync();
            var plan = context.DataIntentPlan;

            // 1. Validate Entities
            foreach (var entityName in plan.Entities)
            {
                var entity = await _catalog.FindEntityAsync(entityName);
                if (entity == null)
                {
                    context.Fail($"The entity '{entityName}' is not defined in the approved catalog.");
                    return;
                }
            }

            // 2. Validate Fields
            var primaryEntity = await _catalog.FindEntityAsync(plan.PrimaryEntity);
            if (primaryEntity == null)
            {
                context.Fail($"The primary entity '{plan.PrimaryEntity}' is missing from the catalog.");
                return;
            }

            context.Entity = primaryEntity;

            foreach (var fieldRef in plan.Fields)
            {
                if (!await CanResolveFieldReferenceAsync(fieldRef, plan, catalog))
                {
                    context.Fail($"The requested field '{fieldRef}' cannot be resolved or is not approved for display.");
                    return;
                }
            }

            // 3. Validate Grouping & Aggregation
            foreach (var groupRef in plan.GroupBy)
            {
                if (!await CanResolveFieldReferenceAsync(groupRef, plan, catalog, "group"))
                {
                    context.Fail($"The grouping field '{groupRef}' is not approved for grouping operations.");
                    return;
                }
            }

            foreach (var agg in plan.Aggregations)
            {
                if (!await CanResolveFieldReferenceAsync(agg.Field, plan, catalog, "aggregate", agg.Entity))
                {
                    context.Fail($"The aggregation field '{agg.Field}' on '{agg.Entity}' is not approved for '{agg.Function}' operations.");
                    return;
                }
            }

            step.SubSteps.Add(new CopilotExecutionStep
            {
                Action = "Catalog capability check",
                Detail = $"Validated {plan.Operation} on {plan.PrimaryEntity} with {plan.Entities.Count} entities and {plan.Fields.Count} fields.",
                Layer = CopilotExecutionLayer.DataPlanning,
                TechnicalData = JsonSerializer.Serialize(new
                {
                    plan.Operation,
                    plan.Entities,
                    plan.Fields,
                    plan.GroupBy,
                    Aggregations = plan.Aggregations.Count
                }),
                Status = CopilotStepStatus.Ok
            });
        }

        private async Task<bool> CanResolveFieldReferenceAsync(
            string fieldRef,
            CopilotDataIntentPlan plan,
            CopilotDataCatalog catalog,
            string? requiredCapability = "display",
            string? entityHint = null)
        {
            var parts = fieldRef.Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var entityName = parts.Length == 2 ? parts[0] : (entityHint ?? plan.PrimaryEntity);
            var fieldName = parts.Length == 2 ? parts[1] : fieldRef;

            var entity = catalog.Entities.FirstOrDefault(e => e.Name.Equals(entityName, StringComparison.OrdinalIgnoreCase));
            if (entity == null) return false;

            var field = entity.Fields.FirstOrDefault(f =>
                f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) ||
                f.Aliases.Any(a => a.Equals(fieldName, StringComparison.OrdinalIgnoreCase)));

            if (field == null) return false;

            if (!string.IsNullOrEmpty(requiredCapability))
            {
                return field.Capabilities.Contains(requiredCapability, StringComparer.OrdinalIgnoreCase);
            }

            return true;
        }
    }
}
