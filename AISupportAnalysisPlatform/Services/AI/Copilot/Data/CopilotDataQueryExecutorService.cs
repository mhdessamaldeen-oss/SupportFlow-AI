using System.Data.Common;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models.AI;
using Microsoft.EntityFrameworkCore;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Executes approved catalog data plans.
    /// The file is intentionally split into three internal layers:
    /// 1. Translator: turns a catalog plan into a provider-neutral query model.
    /// 2. Builder: renders the query model into parameterized SQL.
    /// 3. Projector: shapes raw rows into the copilot response contract.
    /// </summary>
    public class CopilotDataQueryExecutorService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly CopilotDataCatalogService _catalogService;
        private readonly ILogger<CopilotDataQueryExecutorService> _logger;

        public CopilotDataQueryExecutorService(
            IDbContextFactory<ApplicationDbContext> contextFactory,
            CopilotDataCatalogService catalogService,
            ILogger<CopilotDataQueryExecutorService> logger)
        {
            _contextFactory = contextFactory;
            _catalogService = catalogService;
            _logger = logger;
        }

        /// <summary>
        /// Executes one validated metadata-driven plan.
        /// No legacy analytics fallback is attempted here; this executor only returns results
        /// that can be proven from the approved catalog and the supplied plan.
        /// </summary>
        public async Task<AdminCopilotDynamicTicketQueryExecution?> TryExecuteAsync(
            CopilotDataIntentPlan? plan,
            CancellationToken cancellationToken = default)
        {
            if (plan == null || plan.RequiresClarification || string.IsNullOrWhiteSpace(plan.PrimaryEntity))
            {
                return null;
            }

            try
            {
                var catalog = await _catalogService.GetCatalogAsync();
                var queryModel = CatalogQueryTranslator.Translate(plan, catalog);
                if (queryModel == null)
                {
                    return null;
                }

                var sqlPlan = CatalogSqlBuilder.Build(queryModel);
                await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                
                var connection = context.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(cancellationToken);
                }

                await using var dryRunCommand = connection.CreateCommand();
                dryRunCommand.CommandText = $"SET FMTONLY ON;\n{sqlPlan.Sql}\nSET FMTONLY OFF;";
                foreach (var parameter in sqlPlan.Parameters)
                {
                    var dbParameter = dryRunCommand.CreateParameter();
                    dbParameter.ParameterName = parameter.Name;
                    dbParameter.Value = parameter.Value ?? DBNull.Value;
                    dryRunCommand.Parameters.Add(dbParameter);
                }

                try
                {
                    await dryRunCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    plan.ValidationMessages.Add($"SQL Validation Failed: {ex.Message}");
                    return null;
                }

                var rows = await ExecuteRowsAsync(context, sqlPlan, cancellationToken);

                return CatalogResultProjector.Project(plan, queryModel, sqlPlan.Sql, rows);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catalog data query execution failed for approved plan.");
                return null;
            }
        }

        private static async Task<List<AdminCopilotStructuredResultRow>> ExecuteRowsAsync(
            ApplicationDbContext context,
            CatalogSqlPlan sqlPlan,
            CancellationToken cancellationToken)
        {
            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sqlPlan.Sql;
            foreach (var parameter in sqlPlan.Parameters)
            {
                var dbParameter = command.CreateParameter();
                dbParameter.ParameterName = parameter.Name;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                command.Parameters.Add(dbParameter);
            }

            var rows = new List<AdminCopilotStructuredResultRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();

            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new AdminCopilotStructuredResultRow();
                foreach (var column in columns)
                {
                    row.Values[column] = reader[column]?.ToString() ?? "";
                }

                rows.Add(row);
            }

            return rows;
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

        private static string Quote(string identifier)
            => $"[{identifier.Replace("]", "]]")}]";

        private sealed class CatalogQueryTranslator
        {
            public static CatalogQueryModel? Translate(CopilotDataIntentPlan plan, CopilotDataCatalog catalog)
            {
                var primary = FindEntity(catalog, plan.PrimaryEntity);
                if (primary == null)
                {
                    return null;
                }

                var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [primary.Name] = "t0"
                };

                var joins = BuildJoinClauses(plan, catalog, primary, aliases);
                if (joins == null)
                {
                    return null;
                }

                var grouping = BuildGrouping(plan, catalog, primary, aliases);
                if (plan.GroupBy.Count > 0 && grouping.Count == 0)
                {
                    return null;
                }

                var projections = BuildProjections(plan, catalog, primary, aliases, grouping.Any());
                if (projections.Count == 0)
                {
                    return null;
                }

                int parameterIndex = 0;
                var predicates = BuildPredicates(plan, catalog, aliases, ref parameterIndex);
                AddSafetyPredicates(catalog, aliases, predicates);

                var havingPredicates = BuildHavingPredicates(plan, catalog, aliases, projections, ref parameterIndex);

                var orderBy = BuildOrdering(plan, catalog, primary, aliases, projections, grouping.Any());
                var limit = ResolveLimit(plan, projections, grouping.Any());

                return new CatalogQueryModel(
                    primary,
                    aliases[primary.Name],
                    aliases,
                    joins,
                    projections,
                    predicates,
                    havingPredicates,
                    grouping,
                    orderBy,
                    limit,
                    plan.OutputShape,
                    plan.Operation,
                    grouping.Any(),
                    projections.Any(item => item.Kind == CatalogProjectionKind.Aggregation),
                    plan.UseDistinct,
                    aliases.Keys.ToList());
            }

            private static List<CatalogJoinClause>? BuildJoinClauses(
                CopilotDataIntentPlan plan,
                CopilotDataCatalog catalog,
                CopilotEntityDefinition primary,
                Dictionary<string, string> aliases)
            {
                var joins = new List<CatalogJoinClause>();

                foreach (var joinPlan in plan.Joins)
                {
                    var from = FindEntity(catalog, joinPlan.FromEntity);
                    var target = FindEntity(catalog, joinPlan.ToEntity);
                    if (from == null || target == null)
                    {
                        continue;
                    }

                    bool isReverse = false;
                    if (!aliases.TryGetValue(from.Name, out var fromAlias))
                    {
                        if (aliases.TryGetValue(target.Name, out var targetAliasExisting))
                        {
                            var temp = from;
                            from = target;
                            target = temp;
                            fromAlias = targetAliasExisting;
                            isReverse = true;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    if (aliases.ContainsKey(target.Name))
                    {
                        continue;
                    }

                    var relationship = string.IsNullOrWhiteSpace(joinPlan.Relationship) ? null : from.Relationships.FirstOrDefault(rel =>
                        rel.Name.Equals(joinPlan.Relationship, StringComparison.OrdinalIgnoreCase));
                    
                    if (relationship == null && string.IsNullOrWhiteSpace(joinPlan.Relationship))
                    {
                        relationship = from.Relationships.FirstOrDefault(rel =>
                            rel.Target.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
                    }

                    if (relationship == null)
                    {
                        relationship = string.IsNullOrWhiteSpace(joinPlan.Relationship) ? null : target.Relationships.FirstOrDefault(rel =>
                            rel.Name.Equals(joinPlan.Relationship, StringComparison.OrdinalIgnoreCase));
                        
                        if (relationship == null && string.IsNullOrWhiteSpace(joinPlan.Relationship))
                        {
                            relationship = target.Relationships.FirstOrDefault(rel =>
                                rel.Target.Equals(from.Name, StringComparison.OrdinalIgnoreCase));
                        }
                        isReverse = true;
                    }

                    if (relationship == null)
                    {
                        continue;
                    }

                    var alias = $"t{aliases.Count}";
                    aliases[target.Name] = alias;
                    joins.AddRange(BuildJoinFragments(fromAlias, alias, target, relationship, isReverse));
                }

                foreach (var entityName in plan.Entities.Where(entity =>
                             !entity.Equals(primary.Name, StringComparison.OrdinalIgnoreCase) &&
                             !aliases.ContainsKey(entity)))
                {
                    var target = FindEntity(catalog, entityName);
                    var relationship = target == null
                        ? null
                        : primary.Relationships.FirstOrDefault(rel => rel.Target.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
                    
                    bool isReverse = false;
                    if (target != null && relationship == null)
                    {
                        relationship = target.Relationships.FirstOrDefault(rel => rel.Target.Equals(primary.Name, StringComparison.OrdinalIgnoreCase));
                        isReverse = true;
                    }

                    if (target == null || relationship == null)
                    {
                        continue;
                    }

                    var alias = $"t{aliases.Count}";
                    aliases[target.Name] = alias;
                    joins.AddRange(BuildJoinFragments(aliases[primary.Name], alias, target, relationship, isReverse));
                }

                return joins;
            }

            private static IEnumerable<CatalogJoinClause> BuildJoinFragments(
                string fromAlias,
                string targetAlias,
                CopilotEntityDefinition target,
                CopilotRelationshipDefinition relationship,
                bool isReverse = false)
            {
                if (relationship.Type.Equals("ManyToMany", StringComparison.OrdinalIgnoreCase))
                {
                    var viaAlias = $"j{targetAlias[1..]}";
                    var on1 = isReverse 
                        ? $"{fromAlias}.{Quote(relationship.TargetField)} = {viaAlias}.{Quote(relationship.ViaTargetField)}"
                        : $"{fromAlias}.{Quote(relationship.SourceField)} = {viaAlias}.{Quote(relationship.ViaSourceField)}";
                    var on2 = isReverse
                        ? $"{viaAlias}.{Quote(relationship.ViaSourceField)} = {targetAlias}.{Quote(relationship.SourceField)}"
                        : $"{viaAlias}.{Quote(relationship.ViaTargetField)} = {targetAlias}.{Quote(relationship.TargetField)}";

                    return
                    [
                        new CatalogJoinClause($"LEFT JOIN {Quote(relationship.Via)} {viaAlias} ON {on1}"),
                        new CatalogJoinClause($"LEFT JOIN {Quote(target.Table)} {targetAlias} ON {on2}")
                    ];
                }

                var simpleOn = isReverse
                    ? $"{fromAlias}.{Quote(relationship.TargetField)} = {targetAlias}.{Quote(relationship.SourceField)}"
                    : $"{fromAlias}.{Quote(relationship.SourceField)} = {targetAlias}.{Quote(relationship.TargetField)}";

                return
                [
                    new CatalogJoinClause($"LEFT JOIN {Quote(target.Table)} {targetAlias} ON {simpleOn}")
                ];
            }

            private static List<CatalogProjection> BuildProjections(
                CopilotDataIntentPlan plan,
                CopilotDataCatalog catalog,
                CopilotEntityDefinition primary,
                IReadOnlyDictionary<string, string> aliases,
                bool hasGrouping)
            {
                var projections = new List<CatalogProjection>();
                var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var fieldRef in plan.GroupBy)
                {
                    var projection = BuildFieldProjection(fieldRef, catalog, primary, aliases, usedAliases, CatalogProjectionKind.GroupKey);
                    if (projection != null)
                    {
                        projections.Add(projection);
                    }
                }

                var aggregations = plan.Aggregations;

                foreach (var aggregation in aggregations)
                {
                    var projection = BuildAggregationProjection(aggregation, catalog, primary, aliases, usedAliases);
                    if (projection != null)
                    {
                        projections.Add(projection);
                    }
                }

                var selectedFieldRefs = plan.Fields.Count > 0
                    ? plan.Fields
                    : projections.Any(item => item.Kind == CatalogProjectionKind.Aggregation)
                        ? []
                        : primary.DefaultFields.Select(field => $"{primary.Name}.{field}");

                foreach (var fieldRef in selectedFieldRefs)
                {
                    var projection = BuildFieldProjection(fieldRef, catalog, primary, aliases, usedAliases, CatalogProjectionKind.Field);
                    if (projection != null)
                    {
                        projections.Add(projection);
                    }
                }

                if (!projections.Any() && hasGrouping)
                {
                    return [];
                }

                if (projections.Count < 2 && !hasGrouping && !projections.Any(p => p.Kind == CatalogProjectionKind.Aggregation))
                {
                    var defaultRefs = primary.DefaultFields.Select(field => $"{primary.Name}.{field}");
                    foreach (var fieldRef in defaultRefs)
                    {
                        var projection = BuildFieldProjection(fieldRef, catalog, primary, aliases, usedAliases, CatalogProjectionKind.Field);
                        if (projection != null && !projections.Any(p => p.SqlExpression == projection.SqlExpression))
                        {
                            projections.Add(projection);
                        }
                    }
                }

                return projections;
            }

            private static CatalogProjection? BuildFieldProjection(
                string fieldRef,
                CopilotDataCatalog catalog,
                CopilotEntityDefinition primary,
                IReadOnlyDictionary<string, string> aliases,
                HashSet<string> usedAliases,
                CatalogProjectionKind kind)
            {
                var (entityName, fieldName) = SplitFieldRef(fieldRef, primary.Name);
                var entity = FindEntity(catalog, entityName);
                if (entity == null || !aliases.TryGetValue(entity.Name, out var alias))
                {
                    return null;
                }

                var field = FindField(entity, fieldName);
                if (field == null)
                {
                    return null;
                }

                var outputAlias = BuildOutputAlias(entity.Name, field.Name, aliases.Count == 1, usedAliases);
                return new CatalogProjection(
                    $"{alias}.{Quote(field.Name)}",
                    outputAlias,
                    entity.Name,
                    field.Name,
                    kind);
            }

            private static CatalogProjection? BuildAggregationProjection(
                CopilotDataAggregationPlan aggregation,
                CopilotDataCatalog catalog,
                CopilotEntityDefinition primary,
                IReadOnlyDictionary<string, string> aliases,
                HashSet<string> usedAliases)
            {
                var entity = FindEntity(catalog, string.IsNullOrWhiteSpace(aggregation.Entity) ? primary.Name : aggregation.Entity);
                if (entity == null || !aliases.TryGetValue(entity.Name, out var alias))
                {
                    return null;
                }

                var fieldName = string.IsNullOrWhiteSpace(aggregation.Field)
                    ? entity.Fields.FirstOrDefault(field => field.IsKey)?.Name ?? entity.Fields.FirstOrDefault()?.Name ?? ""
                    : aggregation.Field;
                var field = FindField(entity, fieldName);
                if (field == null)
                {
                    return null;
                }

                var function = NormalizeAggregateFunction(aggregation.Function);
                var outputAlias = BuildMetricAlias(aggregation.Alias, function, entity.Name, field.Name, usedAliases);
                return new CatalogProjection(
                    $"{function}({alias}.{Quote(field.Name)})",
                    outputAlias,
                    entity.Name,
                    field.Name,
                    CatalogProjectionKind.Aggregation);
            }

            private static List<CatalogPredicate> BuildPredicates(
                CopilotDataIntentPlan plan,
                CopilotDataCatalog catalog,
                IReadOnlyDictionary<string, string> aliases,
                ref int parameterIndex)
            {
                var predicates = new List<CatalogPredicate>();

                foreach (var filter in plan.Filters)
                {
                    var entity = FindEntity(catalog, filter.Entity);
                    if (entity == null || !aliases.TryGetValue(entity.Name, out var alias))
                    {
                        continue;
                    }

                    var field = FindField(entity, filter.Field);
                    if (field == null)
                    {
                        continue;
                    }

                    var column = $"{alias}.{Quote(field.Name)}";
                    predicates.AddRange(BuildFilterPredicates(column, filter, ref parameterIndex));
                }

                return predicates;
            }

            private static List<CatalogPredicate> BuildHavingPredicates(
                CopilotDataIntentPlan plan,
                CopilotDataCatalog catalog,
                IReadOnlyDictionary<string, string> aliases,
                IReadOnlyList<CatalogProjection> projections,
                ref int parameterIndex)
            {
                var predicates = new List<CatalogPredicate>();

                foreach (var filter in plan.HavingFilters)
                {
                    string column;
                    var metricProjection = projections.FirstOrDefault(projection =>
                        projection.Kind == CatalogProjectionKind.Aggregation &&
                        projection.OutputName.Equals(filter.Field, StringComparison.OrdinalIgnoreCase));
                    
                    if (metricProjection != null)
                    {
                        column = metricProjection.SqlExpression;
                    }
                    else
                    {
                        var entity = FindEntity(catalog, string.IsNullOrWhiteSpace(filter.Entity) ? plan.PrimaryEntity : filter.Entity);
                        if (entity == null || !aliases.TryGetValue(entity.Name, out var alias))
                        {
                            continue;
                        }

                        var field = FindField(entity, filter.Field);
                        if (field == null)
                        {
                            continue;
                        }

                        column = $"{alias}.{Quote(field.Name)}";
                    }

                    predicates.AddRange(BuildFilterPredicates(column, filter, ref parameterIndex));
                }

                return predicates;
            }

            private static List<CatalogPredicate> BuildFilterPredicates(
                string column,
                CopilotDataFilterPlan filter,
                ref int parameterIndex)
            {
                var op = (filter.Operator ?? "").Trim().ToLowerInvariant();
                var predicates = new List<CatalogPredicate>();
                switch (op)
                {
                    case "equals":
                    {
                        var name = $"@p{parameterIndex++}";
                        predicates.Add(new CatalogPredicate($"{column} = {name}", new CatalogSqlParameter(name, filter.Value)));
                        break;
                    }
                    case "contains":
                    {
                        var name = $"@p{parameterIndex++}";
                        predicates.Add(new CatalogPredicate($"{column} LIKE {name}", new CatalogSqlParameter(name, $"%{filter.Value}%")));
                        break;
                    }
                    case "startswith":
                    {
                        var name = $"@p{parameterIndex++}";
                        predicates.Add(new CatalogPredicate($"{column} LIKE {name}", new CatalogSqlParameter(name, $"{filter.Value}%")));
                        break;
                    }
                    case "endswith":
                    {
                        var name = $"@p{parameterIndex++}";
                        predicates.Add(new CatalogPredicate($"{column} LIKE {name}", new CatalogSqlParameter(name, $"%{filter.Value}")));
                        break;
                    }
                    case "gt":
                    case "greaterthan":
                    {
                        var name = $"@p{parameterIndex++}";
                        predicates.Add(new CatalogPredicate($"{column} > {name}", new CatalogSqlParameter(name, filter.Value)));
                        break;
                    }
                    case "gte":
                    case "greaterthanorequal":
                    {
                        var name = $"@p{parameterIndex++}";
                        predicates.Add(new CatalogPredicate($"{column} >= {name}", new CatalogSqlParameter(name, filter.Value)));
                        break;
                    }
                    case "lt":
                    case "lessthan":
                    {
                        var name = $"@p{parameterIndex++}";
                        predicates.Add(new CatalogPredicate($"{column} < {name}", new CatalogSqlParameter(name, filter.Value)));
                        break;
                    }
                    case "lte":
                    case "lessthanorequal":
                    {
                        var name = $"@p{parameterIndex++}";
                        predicates.Add(new CatalogPredicate($"{column} <= {name}", new CatalogSqlParameter(name, filter.Value)));
                        break;
                    }
                    case "between":
                    {
                        var parts = SplitMultiValue(filter.Value?.ToString() ?? "", expected: 2);
                        if (parts.Count != 2)
                        {
                            break;
                        }

                        var fromName = $"@p{parameterIndex++}";
                        var toName = $"@p{parameterIndex++}";
                        predicates.Add(new CatalogPredicate(
                            $"{column} BETWEEN {fromName} AND {toName}",
                            new CatalogSqlParameter(fromName, parts[0]),
                            new CatalogSqlParameter(toName, parts[1])));
                        break;
                    }
                    case "in":
                    {
                        var values = SplitMultiValue(filter.Value?.ToString() ?? "", expected: null);
                        if (values.Count == 0)
                        {
                            break;
                        }

                        var names = new List<string>();
                        var parameters = new List<CatalogSqlParameter>();
                        foreach (var value in values)
                        {
                            var name = $"@p{parameterIndex++}";
                            names.Add(name);
                            parameters.Add(new CatalogSqlParameter(name, value));
                        }

                        predicates.Add(new CatalogPredicate($"{column} IN ({string.Join(", ", names)})", parameters.ToArray()));
                        break;
                    }
                    case "notequals":
                    {
                        var name = $"@p{parameterIndex++}";
                        predicates.Add(new CatalogPredicate($"{column} != {name}", new CatalogSqlParameter(name, filter.Value)));
                        break;
                    }
                    case "isnull":
                    {
                        predicates.Add(new CatalogPredicate($"{column} IS NULL"));
                        break;
                    }
                    case "isnotnull":
                    {
                        predicates.Add(new CatalogPredicate($"{column} IS NOT NULL"));
                        break;
                    }
                }

                return predicates;
            }

            private static void AddSafetyPredicates(
                CopilotDataCatalog catalog,
                IReadOnlyDictionary<string, string> aliases,
                List<CatalogPredicate> predicates)
            {
                foreach (var alias in aliases)
                {
                    var entity = FindEntity(catalog, alias.Key);
                    if (entity == null || entity.Fields.All(field => !field.Name.Equals("IsDeleted", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    predicates.Add(new CatalogPredicate($"{alias.Value}.{Quote("IsDeleted")} = 0"));
                }
            }

            private static List<string> BuildGrouping(
                CopilotDataIntentPlan plan,
                CopilotDataCatalog catalog,
                CopilotEntityDefinition primary,
                IReadOnlyDictionary<string, string> aliases)
            {
                return plan.GroupBy
                    .Select(fieldRef => ResolveColumnExpression(fieldRef, catalog, primary, aliases))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            private static List<CatalogOrderClause> BuildOrdering(
                CopilotDataIntentPlan plan,
                CopilotDataCatalog catalog,
                CopilotEntityDefinition primary,
                IReadOnlyDictionary<string, string> aliases,
                IReadOnlyCollection<CatalogProjection> projections,
                bool hasGrouping)
            {
                var orderBy = new List<CatalogOrderClause>();
                foreach (var sort in plan.Sorts)
                {
                    var metricProjection = projections.FirstOrDefault(projection =>
                        projection.Kind == CatalogProjectionKind.Aggregation &&
                        projection.OutputName.Equals(sort.Field, StringComparison.OrdinalIgnoreCase));
                    if (metricProjection != null)
                    {
                        orderBy.Add(new CatalogOrderClause(Quote(metricProjection.OutputName), sort.Direction));
                        continue;
                    }

                    var projectionByName = projections.FirstOrDefault(projection =>
                        projection.OutputName.Equals(sort.Field, StringComparison.OrdinalIgnoreCase));
                    if (projectionByName != null)
                    {
                        orderBy.Add(new CatalogOrderClause(Quote(projectionByName.OutputName), sort.Direction));
                        continue;
                    }

                    var entity = FindEntity(catalog, string.IsNullOrWhiteSpace(sort.Entity) ? primary.Name : sort.Entity);
                    if (entity == null)
                    {
                        continue;
                    }

                    var field = FindField(entity, sort.Field);
                    if (field == null || !aliases.TryGetValue(entity.Name, out var alias))
                    {
                        continue;
                    }

                    orderBy.Add(new CatalogOrderClause($"{alias}.{Quote(field.Name)}", sort.Direction));
                }

                if (orderBy.Count == 0 && hasGrouping)
                {
                    var metricProjection = projections.FirstOrDefault(projection => projection.Kind == CatalogProjectionKind.Aggregation);
                    if (metricProjection != null)
                    {
                        orderBy.Add(new CatalogOrderClause(Quote(metricProjection.OutputName), SortDirection.Desc));
                    }
                }

                return orderBy;
            }

            private static int? ResolveLimit(CopilotDataIntentPlan plan, IReadOnlyCollection<CatalogProjection> projections, bool hasGrouping)
            {
                if (!plan.Limit.HasValue)
                {
                    return null;
                }

                var scalarOnly = projections.All(item => item.Kind == CatalogProjectionKind.Aggregation) && !hasGrouping;
                return scalarOnly ? null : Math.Clamp(plan.Limit.Value, 1, 500);
            }

            private static string? ResolveColumnExpression(
                string fieldRef,
                CopilotDataCatalog catalog,
                CopilotEntityDefinition primary,
                IReadOnlyDictionary<string, string> aliases)
            {
                var (entityName, fieldName) = SplitFieldRef(fieldRef, primary.Name);
                var entity = FindEntity(catalog, entityName);
                if (entity == null || !aliases.TryGetValue(entity.Name, out var alias))
                {
                    return null;
                }

                var field = FindField(entity, fieldName);
                if (field == null) return null;
                
                if (!string.IsNullOrWhiteSpace(field.SqlExpression))
                {
                    return field.SqlExpression.Contains("{0}") 
                        ? string.Format(field.SqlExpression, alias) 
                        : field.SqlExpression;
                }

                return $"{alias}.{Quote(field.Name)}";
            }

            private static string BuildOutputAlias(string entityName, string fieldName, bool singleEntity, HashSet<string> usedAliases)
            {
                var baseAlias = singleEntity ? fieldName : $"{entityName}_{fieldName}";
                return EnsureUniqueAlias(baseAlias, usedAliases);
            }

            private static string BuildMetricAlias(string explicitAlias, string function, string entityName, string fieldName, HashSet<string> usedAliases)
            {
                var baseAlias = string.IsNullOrWhiteSpace(explicitAlias)
                    ? $"{function}_{entityName}_{fieldName}"
                    : explicitAlias;
                return EnsureUniqueAlias(baseAlias, usedAliases);
            }

            private static string EnsureUniqueAlias(string baseAlias, HashSet<string> usedAliases)
            {
                var sanitized = string.IsNullOrWhiteSpace(baseAlias) ? "Value" : baseAlias.Trim();
                if (usedAliases.Add(sanitized))
                {
                    return sanitized;
                }

                for (var suffix = 2; ; suffix++)
                {
                    var candidate = $"{sanitized}_{suffix}";
                    if (usedAliases.Add(candidate))
                    {
                        return candidate;
                    }
                }
            }

            private static string NormalizeAggregateFunction(string function)
                => (function ?? "").Trim().ToLowerInvariant() switch
                {
                    "avg" or "average" or "mean" => "AVG",
                    "min" or "minimum" => "MIN",
                    "max" or "maximum" => "MAX",
                    "sum" or "total" => "SUM",
                    "count" or "" => "COUNT",
                    _ => "COUNT"
                };

            private static List<string> SplitMultiValue(string value, int? expected)
            {
                var parts = (value ?? "")
                    .Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();

                if (expected.HasValue && parts.Count > expected.Value)
                {
                    return parts.Take(expected.Value).ToList();
                }

                return parts;
            }
        }

        private sealed class CatalogSqlBuilder
        {
            public static CatalogSqlPlan Build(CatalogQueryModel query)
            {
                var selectClause = string.Join(", ", query.Projections.Select(BuildSelectExpression));
                var distinctClause = query.UseDistinct ? "DISTINCT " : "";
                var topClause = query.Limit.HasValue ? $"TOP ({query.Limit.Value}) " : "";
                var sql = $"SELECT {distinctClause}{topClause}{selectClause} FROM {Quote(query.PrimaryEntity.Table)} {query.PrimaryAlias}";

                if (query.Joins.Count > 0)
                {
                    sql += " " + string.Join(" ", query.Joins.Select(join => join.SqlFragment));
                }

                if (query.Predicates.Count > 0)
                {
                    sql += " WHERE " + string.Join(" AND ", query.Predicates.Select(predicate => predicate.SqlFragment));
                }

                if (query.GroupBy.Count > 0)
                {
                    sql += " GROUP BY " + string.Join(", ", query.GroupBy);
                }

                if (query.HavingPredicates.Count > 0)
                {
                    sql += " HAVING " + string.Join(" AND ", query.HavingPredicates.Select(predicate => predicate.SqlFragment));
                }

                if (query.OrderBy.Count > 0)
                {
                    sql += " ORDER BY " + string.Join(", ", query.OrderBy.Select(order => $"{order.SqlExpression} {(order.Direction == SortDirection.Asc ? "ASC" : "DESC")}"));
                }

                var parameters = query.Predicates.SelectMany(predicate => predicate.Parameters).ToList();
                parameters.AddRange(query.HavingPredicates.SelectMany(predicate => predicate.Parameters));
                return new CatalogSqlPlan(sql, parameters);
            }

            private static string BuildSelectExpression(CatalogProjection projection)
                => $"{projection.SqlExpression} AS {Quote(projection.OutputName)}";
        }

        private sealed class CatalogResultProjector
        {
            public static AdminCopilotDynamicTicketQueryExecution Project(
                CopilotDataIntentPlan plan,
                CatalogQueryModel query,
                string sql,
                IReadOnlyList<AdminCopilotStructuredResultRow> rows)
            {
                var columns = query.Projections.Select(projection => projection.OutputName).ToList();
                var totalCount = ResolveTotalCount(query, rows);
                var executionPlan = BuildExecutionPlan(plan, query, columns);

                return new AdminCopilotDynamicTicketQueryExecution
                {
                    Plan = executionPlan,
                    TotalCount = totalCount,
                    StructuredColumns = columns,
                    StructuredRows = rows.ToList(),
                    GeneratedSql = sql,
                    Summary = BuildSummary(plan, query, rows, totalCount),
                    Answer = BuildAnswer(plan, query, rows, totalCount)
                };
            }

            private static AdminCopilotDynamicTicketQueryPlan BuildExecutionPlan(
                CopilotDataIntentPlan plan,
                CatalogQueryModel query,
                IReadOnlyCollection<string> columns)
            {
                var firstAggregation = plan.Aggregations.FirstOrDefault();
                var orderByExpression = query.OrderBy.Count == 0
                    ? null
                    : string.Join(", ", query.OrderBy.Select(order => $"{order.SqlExpression} {(order.Direction == SortDirection.Asc ? "ASC" : "DESC")}"));

                return new AdminCopilotDynamicTicketQueryPlan
                {
                    TargetView = query.PrimaryEntity.Name,
                    Intent = MapIntent(query),
                    Summary = plan.Explanation,
                    MaxResults = query.Limit ?? columns.Count,
                    SelectedColumns = columns.ToList(),
                    HasExplicitColumns = plan.Fields.Count > 0,
                    HasExplicitLimit = plan.Limit.HasValue,
                    HasExplicitGrouping = query.HasGrouping || query.HasAggregation,
                    GroupByField = plan.GroupBy.FirstOrDefault(),
                    AggregationType = plan.Aggregations.Count switch
                    {
                        0 when plan.Operation.Equals("count", StringComparison.OrdinalIgnoreCase) => "count",
                        0 => null,
                        1 => firstAggregation?.Function,
                        _ => "mixed"
                    },
                    AggregationColumn = firstAggregation == null ? null : $"{firstAggregation.Entity}.{firstAggregation.Field}",
                    OrderByExpression = orderByExpression
                };
            }

            private static int ResolveTotalCount(CatalogQueryModel query, IReadOnlyList<AdminCopilotStructuredResultRow> rows)
            {
                if (rows.Count == 0)
                {
                    return 0;
                }

                var scalarProjection = query.Projections.Count == 1
                    ? query.Projections[0]
                    : null;

                if (scalarProjection?.Kind == CatalogProjectionKind.Aggregation &&
                    !query.HasGrouping &&
                    int.TryParse(rows[0].Values.GetValueOrDefault(scalarProjection.OutputName), out var parsed))
                {
                    return parsed;
                }

                return rows.Count;
            }

            private static string BuildSummary(
                CopilotDataIntentPlan plan,
                CatalogQueryModel query,
                IReadOnlyList<AdminCopilotStructuredResultRow> rows,
                int totalCount)
            {
                var entityScope = string.Join(", ", query.InvolvedEntities);
                if (rows.Count == 0)
                {
                    return $"No rows matched the catalog query for {entityScope}.";
                }

                if (query.Projections.Count == 1 && query.Projections[0].Kind == CatalogProjectionKind.Aggregation && !query.HasGrouping)
                {
                    return $"Returned 1 metric value for {entityScope}.";
                }

                if (query.HasGrouping)
                {
                    return $"Returned {rows.Count} grouped row{(rows.Count == 1 ? "" : "s")} for {entityScope}.";
                }

                if (plan.OutputShape.Equals("detail", StringComparison.OrdinalIgnoreCase) && rows.Count == 1)
                {
                    return $"Returned 1 detailed record for {entityScope}.";
                }

                return $"Returned {rows.Count} row{(rows.Count == 1 ? "" : "s")} for {entityScope}.";
            }

            private static string BuildAnswer(
                CopilotDataIntentPlan plan,
                CatalogQueryModel query,
                IReadOnlyList<AdminCopilotStructuredResultRow> rows,
                int totalCount)
            {
                if (rows.Count == 0)
                {
                    return "No approved data matched the request.";
                }

                if (query.Projections.Count == 1 && query.Projections[0].Kind == CatalogProjectionKind.Aggregation && !query.HasGrouping)
                {
                    var output = query.Projections[0].OutputName;
                    return $"{Humanize(output)}: {rows[0].Values.GetValueOrDefault(output, "")}.";
                }

                if (plan.OutputShape.Equals("detail", StringComparison.OrdinalIgnoreCase) && rows.Count == 1)
                {
                    var detail = string.Join(", ", rows[0].Values.Select(item => $"{Humanize(item.Key)}: {item.Value}"));
                    return detail;
                }

                var columnSummary = string.Join(", ", query.Projections.Select(projection => Humanize(projection.OutputName)).Take(4));
                return $"Returned {rows.Count} row{(rows.Count == 1 ? "" : "s")} with {columnSummary}.";
            }

            private static string Humanize(string value)
                => (value ?? "").Replace('_', ' ').Trim();

            private static DynamicQueryIntent MapIntent(CatalogQueryModel query)
            {
                if (query.HasGrouping)
                {
                    return DynamicQueryIntent.GroupBy;
                }

                if (query.Projections.Count > 0 && query.Projections.All(projection => projection.Kind == CatalogProjectionKind.Aggregation))
                {
                    return DynamicQueryIntent.Summarize;
                }

                return DynamicQueryIntent.List;
            }
        }

        private sealed record CatalogQueryModel(
            CopilotEntityDefinition PrimaryEntity,
            string PrimaryAlias,
            IReadOnlyDictionary<string, string> Aliases,
            IReadOnlyList<CatalogJoinClause> Joins,
            IReadOnlyList<CatalogProjection> Projections,
            IReadOnlyList<CatalogPredicate> Predicates,
            IReadOnlyList<CatalogPredicate> HavingPredicates,
            IReadOnlyList<string> GroupBy,
            IReadOnlyList<CatalogOrderClause> OrderBy,
            int? Limit,
            string OutputShape,
            string Operation,
            bool HasGrouping,
            bool HasAggregation,
            bool UseDistinct,
            IReadOnlyList<string> InvolvedEntities);

        private sealed record CatalogJoinClause(string SqlFragment);

        private sealed record CatalogProjection(
            string SqlExpression,
            string OutputName,
            string SourceEntity,
            string SourceField,
            CatalogProjectionKind Kind);

        private sealed record CatalogOrderClause(string SqlExpression, SortDirection Direction);

        private sealed record CatalogPredicate(string SqlFragment, params CatalogSqlParameter[] Parameters);

        private sealed record CatalogSqlPlan(string Sql, List<CatalogSqlParameter> Parameters);

        private sealed record CatalogSqlParameter(string Name, object Value);

        private enum CatalogProjectionKind
        {
            Field,
            GroupKey,
            Aggregation
        }
    }
}
