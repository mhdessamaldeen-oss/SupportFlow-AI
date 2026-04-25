using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Translates a validated catalog data plan into the narrower pipeline plan contract.
    /// This bridge is intentionally conservative: if a request needs multi-hop joins,
    /// non-equality filters, or richer catalog semantics, the translator returns null
    /// so the caller can stay on the primary catalog execution path instead of guessing.
    /// </summary>
    public class CopilotDataIntentPlanTranslatorService
    {
        private readonly CopilotDataCatalogService _catalogService;

        public CopilotDataIntentPlanTranslatorService(CopilotDataCatalogService catalogService)
        {
            _catalogService = catalogService;
        }

        public async Task<AdminCopilotDynamicTicketQueryPlan?> TranslateAsync(
            CopilotDataIntentPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (plan.RequiresClarification || string.IsNullOrWhiteSpace(plan.PrimaryEntity))
            {
                return null;
            }

            var catalog = await _catalogService.GetCatalogAsync();
            var primary = FindEntity(catalog, plan.PrimaryEntity);
            if (primary == null)
            {
                return null;
            }

            if (!CanTranslateToPipelineShape(plan, primary, catalog))
            {
                return null;
            }

            var selectedColumns = new List<string>();
            foreach (var fieldRef in plan.Fields)
            {
                var mappedField = TryMapFieldReference(fieldRef, primary, catalog);
                if (mappedField == null)
                {
                    return null;
                }

                selectedColumns.Add(mappedField);
            }

            var translated = new AdminCopilotDynamicTicketQueryPlan
            {
                TargetView = primary.Name,
                Intent = TranslateIntent(plan),
                Summary = plan.Explanation ?? "",
                RequiresClarification = plan.RequiresClarification,
                ClarificationQuestion = plan.ClarificationQuestion ?? "",
                MaxResults = Math.Clamp(plan.Limit ?? primary.DefaultLimit, 1, Math.Max(primary.MaxLimit, 1)),
                SelectedColumns = selectedColumns,
                HasExplicitColumns = selectedColumns.Count > 0,
                HasExplicitLimit = plan.Limit.HasValue
            };

            if (!TranslateGrouping(plan, primary, catalog, translated))
            {
                return null;
            }

            if (!TranslateAggregation(plan, primary, catalog, translated))
            {
                return null;
            }

            if (!TranslateSorting(plan, primary, catalog, translated))
            {
                return null;
            }

            if (!TranslateFilters(plan, primary, catalog, translated))
            {
                return null;
            }

            return translated;
        }

        private static bool CanTranslateToPipelineShape(
            CopilotDataIntentPlan plan,
            CopilotEntityDefinition primary,
            CopilotDataCatalog catalog)
        {
            if (plan.Joins.Count > 1)
            {
                return false;
            }

            foreach (var entityName in plan.Entities.Where(entity =>
                         !entity.Equals(primary.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (!HasDirectDefaultJoin(primary, entityName, catalog))
                {
                    return false;
                }
            }

            foreach (var filter in plan.Filters)
            {
                if (!IsPipelineSupportedOperator(filter.Operator) ||
                    !CanMapFieldReference($"{filter.Entity}.{filter.Field}", primary, catalog))
                {
                    return false;
                }
            }

            foreach (var fieldRef in plan.Fields.Concat(plan.GroupBy))
            {
                if (!CanMapFieldReference(fieldRef, primary, catalog))
                {
                    return false;
                }
            }

            foreach (var sort in plan.Sorts)
            {
                if (!string.IsNullOrWhiteSpace(sort.Entity) &&
                    !CanMapFieldReference($"{sort.Entity}.{sort.Field}", primary, catalog) &&
                    !plan.Aggregations.Any(aggregation => aggregation.Alias.Equals(sort.Field, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            foreach (var aggregation in plan.Aggregations)
            {
                if (!aggregation.Function.Equals("count", StringComparison.OrdinalIgnoreCase) &&
                    !CanMapFieldReference($"{aggregation.Entity}.{aggregation.Field}", primary, catalog))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TranslateGrouping(
            CopilotDataIntentPlan plan,
            CopilotEntityDefinition primary,
            CopilotDataCatalog catalog,
            AdminCopilotDynamicTicketQueryPlan translated)
        {
            if (plan.GroupBy.Count == 0)
            {
                return true;
            }

            var groupField = TryMapFieldReference(plan.GroupBy[0], primary, catalog);
            if (groupField == null)
            {
                return false;
            }

            translated.GroupByField = groupField;
            translated.HasExplicitGrouping = true;
            return true;
        }

        private static bool TranslateAggregation(
            CopilotDataIntentPlan plan,
            CopilotEntityDefinition primary,
            CopilotDataCatalog catalog,
            AdminCopilotDynamicTicketQueryPlan translated)
        {
            if (plan.Aggregations.Count == 0)
            {
                return true;
            }

            var aggregation = plan.Aggregations[0];
            translated.AggregationType = aggregation.Function;

            if (aggregation.Function.Equals("count", StringComparison.OrdinalIgnoreCase))
            {
                translated.AggregationColumn = string.IsNullOrWhiteSpace(aggregation.Field)
                    ? primary.Fields.FirstOrDefault(field => field.IsKey)?.Name ?? "Id"
                    : TryMapFieldReference($"{aggregation.Entity}.{aggregation.Field}", primary, catalog) ?? aggregation.Field;
                return true;
            }

            var aggregationColumn = TryMapFieldReference($"{aggregation.Entity}.{aggregation.Field}", primary, catalog);
            if (aggregationColumn == null)
            {
                return false;
            }

            translated.AggregationColumn = aggregationColumn;
            return true;
        }

        private static bool TranslateSorting(
            CopilotDataIntentPlan plan,
            CopilotEntityDefinition primary,
            CopilotDataCatalog catalog,
            AdminCopilotDynamicTicketQueryPlan translated)
        {
            if (plan.Sorts.Count == 0)
            {
                return true;
            }

            var sort = plan.Sorts[0];
            var mappedSort = plan.Aggregations.Any(aggregation =>
                aggregation.Alias.Equals(sort.Field, StringComparison.OrdinalIgnoreCase))
                ? sort.Field
                : TryMapFieldReference($"{sort.Entity}.{sort.Field}", primary, catalog);

            if (mappedSort == null)
            {
                return false;
            }

            translated.SortBy = mappedSort;
            translated.SortDirection = sort.Direction;
            translated.HasExplicitSort = true;
            return true;
        }

        private static bool TranslateFilters(
            CopilotDataIntentPlan plan,
            CopilotEntityDefinition primary,
            CopilotDataCatalog catalog,
            AdminCopilotDynamicTicketQueryPlan translated)
        {
            foreach (var filter in plan.Filters)
            {
                if (filter.Operator.Equals("between", StringComparison.OrdinalIgnoreCase) &&
                    filter.Entity.Equals(primary.Name, StringComparison.OrdinalIgnoreCase) &&
                    filter.Field.Equals("CreatedAt", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = (filter.Value?.ToString() ?? "")
                        .Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 &&
                        DateTime.TryParse(parts[0], out var fromDate) &&
                        DateTime.TryParse(parts[1], out var toDate))
                    {
                        translated.AbsoluteStartDateUtc = fromDate;
                        translated.AbsoluteEndDateUtc = toDate;
                        translated.HasExplicitDateRange = true;
                        continue;
                    }

                    return false;
                }

                var mappedField = TryMapFieldReference($"{filter.Entity}.{filter.Field}", primary, catalog);
                if (mappedField == null)
                {
                    return false;
                }

                if (mappedField.Equals("StatusName", StringComparison.OrdinalIgnoreCase))
                {
                    var values = SplitValues(filter.Value);
                    translated.StatusNames = values;
                    continue;
                }

                if (!filter.Operator.Equals("equals", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                translated.GlobalFilters[mappedField] = filter.Value?.ToString() ?? "";
            }

            return true;
        }

        private static DynamicQueryIntent TranslateIntent(CopilotDataIntentPlan plan)
            => (plan.Operation ?? "").Trim().ToLowerInvariant() switch
            {
                "count" => DynamicQueryIntent.Count,
                "breakdown" or "compare" when plan.GroupBy.Count > 0 => DynamicQueryIntent.GroupBy,
                "aggregate" => DynamicQueryIntent.Scalar,
                "detail" => DynamicQueryIntent.Detail,
                _ => DynamicQueryIntent.List
            };

        private static bool IsPipelineSupportedOperator(string? @operator)
            => (@operator ?? "").Trim().ToLowerInvariant() is "equals" or "in" or "between";

        private static bool CanMapFieldReference(string fieldRef, CopilotEntityDefinition primary, CopilotDataCatalog catalog)
            => TryMapFieldReference(fieldRef, primary, catalog) != null;

        private static string? TryMapFieldReference(string fieldRef, CopilotEntityDefinition primary, CopilotDataCatalog catalog)
        {
            var (entityName, fieldName) = SplitFieldRef(fieldRef, primary.Name);
            var entity = FindEntity(catalog, entityName);
            if (entity == null)
            {
                return null;
            }

            if (entity.Name.Equals(primary.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (FindField(entity, fieldName) != null)
                {
                    return FindField(entity, fieldName)!.Name;
                }

                var displayProjection = TryResolveDisplayProjection(primary, fieldName, catalog);
                return displayProjection;
            }

            var relationship = primary.Relationships.FirstOrDefault(rel =>
                rel.IsDefaultJoin &&
                rel.Target.Equals(entity.Name, StringComparison.OrdinalIgnoreCase));
            if (relationship == null)
            {
                return null;
            }

            var targetField = FindField(entity, fieldName);
            if (targetField == null)
            {
                return fieldName.Equals("Name", StringComparison.OrdinalIgnoreCase)
                    ? $"{relationship.Name}Name"
                    : null;
            }

            if (targetField.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                return $"{relationship.Name}Name";
            }

            return $"{relationship.Name}.{targetField.Name}";
        }

        private static string? TryResolveDisplayProjection(
            CopilotEntityDefinition primary,
            string fieldName,
            CopilotDataCatalog catalog)
        {
            var relationship = primary.Relationships.FirstOrDefault(rel =>
                rel.IsDefaultJoin &&
                fieldName.Equals($"{rel.Name}Name", StringComparison.OrdinalIgnoreCase));
            if (relationship == null)
            {
                return null;
            }

            var target = FindEntity(catalog, relationship.Target);
            if (target == null)
            {
                return null;
            }

            if (target.Fields.Any(field => field.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)))
            {
                return $"{relationship.Name}Name";
            }

            if (target.Fields.Any(field => field.Name.Equals("FirstName", StringComparison.OrdinalIgnoreCase)) ||
                target.Fields.Any(field => field.Name.Equals("LastName", StringComparison.OrdinalIgnoreCase)) ||
                target.Fields.Any(field => field.Name.Equals("UserName", StringComparison.OrdinalIgnoreCase)))
            {
                return $"{relationship.Name}Name";
            }

            return null;
        }

        private static bool HasDirectDefaultJoin(CopilotEntityDefinition primary, string targetEntityName, CopilotDataCatalog catalog)
        {
            var target = FindEntity(catalog, targetEntityName);
            return target != null && primary.Relationships.Any(rel =>
                rel.IsDefaultJoin &&
                rel.Target.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        }

        private static CopilotEntityDefinition? FindEntity(CopilotDataCatalog catalog, string entityName)
            => catalog.Entities.FirstOrDefault(entity =>
                entity.Name.Equals(entityName, StringComparison.OrdinalIgnoreCase) ||
                entity.Table.Equals(entityName, StringComparison.OrdinalIgnoreCase));

        private static CopilotFieldDefinition? FindField(CopilotEntityDefinition entity, string fieldName)
            => entity.Fields.FirstOrDefault(field =>
                field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) ||
                field.Aliases.Any(alias => alias.Equals(fieldName, StringComparison.OrdinalIgnoreCase)));

        private static (string Entity, string Field) SplitFieldRef(string fieldRef, string fallbackEntity)
        {
            var parts = (fieldRef ?? "").Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 2 ? (parts[0], parts[1]) : (fallbackEntity, fieldRef ?? "");
        }

        private static List<string> SplitValues(object? value)
            => (value?.ToString() ?? "")
                .Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
    }
}
