using System.Text.Json;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using AISupportAnalysisPlatform.Services.AI.Pipeline.Abstractions;

namespace AISupportAnalysisPlatform.Services.AI.Pipeline.Analysis
{
    /// <summary>
    /// Analyzer step for the catalog analytics pipeline.
    /// It maps natural language questions to formal metadata-grounded data intent plans.
    /// </summary>
    public class PipelineAnalyzerService : IAnalyticsStep
    {
        private readonly ICopilotQuestionPreprocessor _preprocessor;
        private readonly CopilotDataIntentPlannerService _dataIntentPlanner;
        private readonly CopilotDataIntentPlanTranslatorService _translator;
        private readonly CopilotDataCatalogService _catalogService;

        public int Order => 10;

        public PipelineAnalyzerService(
            ICopilotQuestionPreprocessor preprocessor,
            CopilotDataIntentPlannerService dataIntentPlanner,
            CopilotDataIntentPlanTranslatorService translator,
            CopilotDataCatalogService catalogService)
        {
            _preprocessor = preprocessor;
            _dataIntentPlanner = dataIntentPlanner;
            _translator = translator;
            _catalogService = catalogService;
        }

        public async Task ExecuteAsync(AnalyticsPipelineContext context)
        {
            var step = new CopilotExecutionStep
            {
                Action = "Analyze catalog intent",
                Detail = "Map the natural language question to a formal metadata-grounded data intent plan using approved entities and fields.",
                Layer = CopilotExecutionLayer.DataPlanning
            };
            context.ExecutionSteps.Add(step);

            var request = new CopilotChatRequest { Question = context.RawQuestion };
            var questionContext = _preprocessor.Process(request);
            var dataPlan = await _dataIntentPlanner.BuildAsync(request, questionContext, context.CancellationToken);

            if (questionContext.IsFollowUpQuestion && questionContext.ConversationContext?.PreviousDataIntentPlan != null)
            {
                var previousPlan = questionContext.ConversationContext.PreviousDataIntentPlan;
                
                foreach (var filter in dataPlan.Filters)
                {
                    if (!previousPlan.Filters.Any(f => f.Entity.Equals(filter.Entity, StringComparison.OrdinalIgnoreCase) && f.Field.Equals(filter.Field, StringComparison.OrdinalIgnoreCase)))
                    {
                        previousPlan.Filters.Add(filter);
                    }
                }
                
                if (dataPlan.Sorts.Count > 0)
                {
                    previousPlan.Sorts = dataPlan.Sorts;
                }

                foreach (var planEntity in dataPlan.Entities)
                {
                    if (!previousPlan.Entities.Contains(planEntity, StringComparer.OrdinalIgnoreCase))
                    {
                        previousPlan.Entities.Add(planEntity);
                    }
                }

                foreach (var join in dataPlan.Joins)
                {
                    if (!previousPlan.Joins.Any(j => j.ToEntity.Equals(join.ToEntity, StringComparison.OrdinalIgnoreCase)))
                    {
                        previousPlan.Joins.Add(join);
                    }
                }

                dataPlan = previousPlan;
            }

            context.DataIntentPlan = dataPlan;

            step.SubSteps.Add(new CopilotExecutionStep
            {
                Action = "Build catalog data plan",
                Detail = dataPlan.RequiresClarification
                    ? "The metadata planner needs clarification before a safe query can run."
                    : $"Prepared {dataPlan.Operation} on {dataPlan.PrimaryEntity} using {dataPlan.Entities.Count} approved catalog entities.",
                Layer = CopilotExecutionLayer.DataPlanning,
                TechnicalData = JsonSerializer.Serialize(dataPlan),
                Status = dataPlan.RequiresClarification ? CopilotStepStatus.Warn : CopilotStepStatus.Ok
            });

            if (dataPlan.RequiresClarification || string.IsNullOrWhiteSpace(dataPlan.PrimaryEntity))
            {
                context.Fail(dataPlan.ClarificationQuestion);
                return;
            }

            var entity = await _catalogService.FindEntityAsync(dataPlan.PrimaryEntity);
            if (entity == null)
            {
                context.Fail($"The selected catalog entity '{dataPlan.PrimaryEntity}' is not available in the approved metadata.");
                return;
            }

            context.Entity = entity;
            context.TargetViewName = entity.Name;

            // Attempt translation for backward compatibility in UI result structures.
            var translatedPlan = await _translator.TranslateAsync(dataPlan, context.CancellationToken);
            if (translatedPlan != null)
            {
                context.Plan = translatedPlan;
                step.SubSteps.Add(new CopilotExecutionStep
                {
                    Action = "Map to UI plan",
                    Detail = $"Mapped the shared catalog plan into a UI-ready representation for {entity.Name}.",
                    Layer = CopilotExecutionLayer.DataPlanning,
                    Status = CopilotStepStatus.Ok
                });
            }
            else
            {
                context.Plan = BuildBasicUiPlan(dataPlan);
            }
        }

        private static AdminCopilotDynamicTicketQueryPlan BuildBasicUiPlan(CopilotDataIntentPlan plan)
        {
            return new AdminCopilotDynamicTicketQueryPlan
            {
                TargetView = plan.PrimaryEntity,
                Summary = plan.Explanation,
                Intent = plan.Operation.ToLowerInvariant() switch
                {
                    "count" => DynamicQueryIntent.Count,
                    "groupby" => DynamicQueryIntent.GroupBy,
                    "aggregate" => DynamicQueryIntent.Summarize,
                    _ => DynamicQueryIntent.List
                },
                SelectedColumns = plan.Fields,
                GroupByField = plan.GroupBy.FirstOrDefault(),
                AggregationType = plan.Aggregations.FirstOrDefault()?.Function,
                AggregationColumn = plan.Aggregations.FirstOrDefault()?.Field
            };
        }
    }
}
