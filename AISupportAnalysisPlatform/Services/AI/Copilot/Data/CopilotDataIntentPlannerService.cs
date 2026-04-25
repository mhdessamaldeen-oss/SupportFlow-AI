using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Services.AI.Providers;
using Microsoft.EntityFrameworkCore;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Uses the approved data catalog plus the active AI provider to understand database questions.
    /// This service plans data access only; execution is handled separately after validation.
    /// </summary>
    public class CopilotDataIntentPlannerService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly IAiProviderFactory _providerFactory;
        private readonly CopilotDataCatalogService _catalogService;
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly CopilotLexicalMatcherService _lexicalMatcher;
        private readonly CopilotTextCatalog _text;
        private readonly ILogger<CopilotDataIntentPlannerService> _logger;

        public CopilotDataIntentPlannerService(
            IAiProviderFactory providerFactory,
            CopilotDataCatalogService catalogService,
            IDbContextFactory<ApplicationDbContext> contextFactory,
            CopilotLexicalMatcherService lexicalMatcher,
            CopilotTextCatalog text,
            ILogger<CopilotDataIntentPlannerService> logger)
        {
            _providerFactory = providerFactory;
            _catalogService = catalogService;
            _contextFactory = contextFactory;
            _lexicalMatcher = lexicalMatcher;
            _text = text;
            _logger = logger;
        }

        /// <summary>
        /// Builds a catalog-grounded plan for an admin data question and validates every selected metadata item.
        /// </summary>
        public async Task<CopilotDataIntentPlan> BuildAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CancellationToken cancellationToken = default)
        {
            var catalog = await _catalogService.GetCatalogAsync();
            
            var deterministicTask = TryBuildDeterministicPlanAsync(request.Question, catalog, cancellationToken);
            var modelTask = TryBuildWithModelAsync(request, questionContext, catalog, cancellationToken);

            await Task.WhenAll(deterministicTask, modelTask);

            var deterministicPlan = deterministicTask.Result;
            var modelPlan = modelTask.Result;

            if (deterministicPlan != null)
            {
                deterministicPlan = await ValidateAndNormalizeAsync(deterministicPlan, catalog, request.Question, cancellationToken);
            }

            if (modelPlan != null)
            {
                modelPlan = await ValidateAndNormalizeAsync(modelPlan, catalog, request.Question, cancellationToken);
            }

            var detScore = deterministicPlan != null ? ScorePlan(deterministicPlan, catalog) : -999;
            var modelScore = modelPlan != null ? ScorePlan(modelPlan, catalog) : -999;

            var bestPlan = detScore >= modelScore ? deterministicPlan : modelPlan;
            
            if (bestPlan == null)
            {
                bestPlan = await BuildFallbackPlanAsync(request.Question, catalog, cancellationToken);
                return await ValidateAndNormalizeAsync(bestPlan, catalog, request.Question, cancellationToken);
            }

            return bestPlan;
        }

        private int ScorePlan(CopilotDataIntentPlan plan, CopilotDataCatalog catalog)
        {
            if (plan == null) return -999;
            
            int score = 0;
            if (!string.IsNullOrWhiteSpace(plan.PrimaryEntity) && FindEntity(catalog, plan.PrimaryEntity) != null) score += 5;
            
            score += plan.Fields.Count * 3;
            score += plan.Filters.Count * 3;
            
            if (plan.Aggregations.Count > 0) score += 5;
            
            score += plan.Joins.Count * 2;
            
            score -= plan.ValidationMessages.Count * 5;
            if (plan.RequiresClarification) score -= 10;
            
            return score;
        }

        private async Task<CopilotDataIntentPlan?> TryBuildWithModelAsync(
            CopilotChatRequest request,
            CopilotQuestionContext questionContext,
            CopilotDataCatalog catalog,
            CancellationToken cancellationToken)
        {
            if (_text.DataIntentPlannerPromptLines.Count == 0)
            {
                return null;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var provider = _providerFactory.GetActiveProvider();
                var catalogJson = BuildRelevantCatalogContext(request.Question, catalog);
                var prompt = CopilotTextTemplate.Apply(
                    CopilotTextTemplate.JoinLines(_text.DataIntentPlannerPromptLines),
                    new Dictionary<string, string?>
                    {
                        ["DATA_CATALOG"] = catalogJson,
                        ["CONVERSATION_CONTEXT"] = questionContext.ConversationSummary,
                        ["KNOWN_VALUES"] = await GetKnownValuesAsync(catalog, cancellationToken),
                        ["QUESTION"] = request.Question
                    });

                var generateTask = provider.GenerateAsync(prompt);
                var completedTask = await Task.WhenAny(generateTask, Task.Delay(TimeSpan.FromSeconds(45), cancellationToken));
                if (completedTask != generateTask)
                {
                    _logger.LogWarning("Catalog data intent planning via model timed out after 45 seconds.");
                    return null;
                }

                var result = await generateTask;
                if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
                {
                    return null;
                }

                var rawResponse = result.ResponseText;
                try
                {
                    var json = CopilotJsonHelper.ExtractJson(rawResponse);
                    return JsonSerializer.Deserialize<CopilotDataIntentPlan>(json, JsonOptions);
                }
                catch
                {
                    try
                    {
                        var fixPrompt = $"Fix this JSON so it can be parsed correctly. Return ONLY valid JSON:\n\n{rawResponse}";
                        var fixTask = provider.GenerateAsync(fixPrompt);
                        var fixResult = await Task.WhenAny(fixTask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
                        
                        if (fixResult == fixTask)
                        {
                            var fixResponse = await fixTask;
                            if (fixResponse.Success && !string.IsNullOrWhiteSpace(fixResponse.ResponseText))
                            {
                                var fixedJson = CopilotJsonHelper.ExtractJson(fixResponse.ResponseText);
                                return JsonSerializer.Deserialize<CopilotDataIntentPlan>(fixedJson, JsonOptions);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore secondary failure
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catalog data intent planning via model failed.");
                return null;
            }
        }

        private string BuildRelevantCatalogContext(string question, CopilotDataCatalog catalog)
        {
            var normalized = CopilotLexicalMatcherService.Normalize(question);
            
            var topEntities = catalog.Entities
                .Select(entity => new
                {
                    Entity = entity,
                    Score = ScoreEntityMatch(normalized, entity)
                })
                .OrderByDescending(item => item.Score)
                .Take(4)
                .Select(item => item.Entity)
                .ToList();

            if (topEntities.Count == 0)
            {
                return JsonSerializer.Serialize(catalog.Entities.Select(e => new { e.Name, e.Description, e.Fields, e.Relationships }), JsonOptions);
            }

            var relevantEntities = topEntities.Select(e => new 
            {
                e.Name,
                e.Description,
                e.Aliases,
                e.AllowedOperations,
                e.DefaultLimit,
                e.DefaultFields,
                e.Fields,
                e.Relationships
            });

            return JsonSerializer.Serialize(relevantEntities, JsonOptions);
        }

        private async Task<CopilotDataIntentPlan?> TryBuildDeterministicPlanAsync(
            string question,
            CopilotDataCatalog catalog,
            CancellationToken cancellationToken)
        {
            var normalized = CopilotLexicalMatcherService.Normalize(question);
            var entityMatches = catalog.Entities
                .Select(entity => new
                {
                    Entity = entity,
                    Score = ScoreEntityMatch(normalized, entity),
                    Position = FindEntityMatchPosition(normalized, entity)
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Position)
                .ToList();

            if (entityMatches.Count == 0)
            {
                return null;
            }

            var primary = entityMatches.OrderBy(e => e.Position).First().Entity;
            var plan = new CopilotDataIntentPlan
            {
                PrimaryEntity = primary.Name,
                Entities = [primary.Name],
                OutputShape = "table",
                Explanation = "Metadata-first deterministic catalog plan.",
                Limit = ExtractLimit(normalized, primary.DefaultLimit)
            };

            var groupedFields = await ResolveGroupingFieldsAsync(normalized, catalog, primary, cancellationToken);
            if (groupedFields.Any())
            {
                foreach(var groupedField in groupedFields)
                {
                    plan.GroupBy.Add(groupedField);
                }
                plan.Operation = plan.Operation is "count" or "list" ? "breakdown" : plan.Operation;
            }

            var aggregatePlan = await ResolveAggregationAsync(normalized, catalog, primary, cancellationToken);
            if (aggregatePlan != null)
            {
                plan.Aggregations.Add(aggregatePlan);
            }
            else if (Regex.IsMatch(normalized, @"\b(average|avg|sum|max|min|highest|lowest)\b", RegexOptions.IgnoreCase))
            {
                // DEEP DIVE: If the user explicitly asked for an aggregation, but we couldn't resolve the field mathematically,
                // do NOT silently degrade to a list. Ask for clarification!
                plan.RequiresClarification = true;
                plan.ClarificationQuestion = "You asked for a calculation (like average, sum, max), but I couldn't find a matching numerical field in the catalog to calculate. Could you clarify the exact data field?";
            }

            if (plan.GroupBy.Count > 0)
            {
                plan.Operation = "breakdown";
            }
            else if (plan.Aggregations.Count > 0 || ContainsPhrase(normalized, "count") || ContainsPhrase(normalized, "how many") || ContainsPhrase(normalized, "total"))
            {
                plan.Operation = plan.Aggregations.Count == 0 && plan.GroupBy.Count == 0 && !ContainsPhrase(normalized, "count") ? "list" : "aggregate";
                if (plan.Aggregations.Count == 0)
                {
                    plan.Operation = "count";
                }
            }
            else
            {
                plan.Operation = "list";
            }

            if (plan.Operation == "count" || plan.Operation == "aggregate")
            {
                plan.OutputShape = "Metric";
            }
            else if (ContainsPhrase(normalized, "detail") || ContainsPhrase(normalized, "details") || ContainsPhrase(normalized, "more about"))
            {
                plan.Operation = "detail";
                plan.OutputShape = "Detail";
            }
            else
            {
                plan.OutputShape = "Table";
            }

            await ApplyMetadataFiltersAsync(plan, normalized, catalog, primary, cancellationToken);
            
            // W-2: If we found multiple entities or complex filters but no join path, the deterministic plan is invalid.
            if (plan.Entities.Count > 1 && plan.Joins.Count == 0)
            {
                return null;
            }

            EnsureDefaultAggregation(plan, primary);
            EnsureDefaultFields(plan, catalog, primary);

            if (plan.Filters.Count == 0 && plan.GroupBy.Count == 0 && plan.Aggregations.Count == 0 &&
                entityMatches.Count == 1 && primary.Name.Equals("User", StringComparison.OrdinalIgnoreCase) &&
                !normalized.Contains("user", StringComparison.OrdinalIgnoreCase) &&
                !normalized.Contains("users", StringComparison.OrdinalIgnoreCase))
            {
                plan.RequiresClarification = true;
                plan.ClarificationQuestion = "I need the specific approved data area you want to query.";
            }

            return plan;
        }





        private Task<CopilotDataIntentPlan> BuildFallbackPlanAsync(
            string question,
            CopilotDataCatalog catalog,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new CopilotDataIntentPlan
            {
                Operation = "list",
                OutputShape = "table",
                PrimaryEntity = "",
                Entities = [],
                Fields = [],
                RequiresClarification = true,
                ClarificationQuestion = "I could not safely determine a data plan for this request. Please clarify which approved data area or fields you want to query.",
                Explanation = "Fallback catalog plan after model data planning was unavailable."
            });
        }





        private static int? ExtractLimit(string normalizedQuestion, int defaultLimit)
        {
            var topMatch = Regex.Match(normalizedQuestion, @"\b(?:top|latest|first|show(?:\s+me)?|list(?:\s+me)?|give(?:\s+me)?)\s+(\d+)\b", RegexOptions.IgnoreCase);
            if (topMatch.Success && int.TryParse(topMatch.Groups[1].Value, out var parsedTop))
            {
                return parsedTop;
            }

            var numericLead = Regex.Match(normalizedQuestion, @"^\s*(\d+)\b");
            if (numericLead.Success && int.TryParse(numericLead.Groups[1].Value, out var parsedLead))
            {
                return parsedLead;
            }

            return defaultLimit <= 0 ? 10 : defaultLimit;
        }

        private async Task<IEnumerable<string>> ResolveGroupingFieldsAsync(
            string normalizedQuestion,
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupPhraseMatch = Regex.Match(
                normalizedQuestion,
                @"\b(?:by|per)\s+([a-z0-9 _,-]+(?:and\s+[a-z0-9 _,-]+)*)(?:\bwith\b|\bwhere\b|\bfrom\b|\bin\b|$)",
                RegexOptions.IgnoreCase);
            if (!groupPhraseMatch.Success)
            {
                return Array.Empty<string>();
            }

            var phrase = CopilotLexicalMatcherService.Normalize(groupPhraseMatch.Groups[1].Value);
            var parts = phrase.Split(new[] { " and ", " or ", "," }, StringSplitOptions.RemoveEmptyEntries);
            
            var results = new List<string>();
            foreach(var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                var resolved = await ResolveFieldReferenceAsync(primaryEntity, catalog, part.Trim(), preferredCapability: "group", cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolved) && !results.Contains(resolved))
                {
                    results.Add(resolved);
                }
            }
            return results;
        }

        private async Task<CopilotDataAggregationPlan?> ResolveAggregationAsync(
            string normalizedQuestion,
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var aggregateField = await ResolveAggregateFieldAsync(normalizedQuestion, catalog, primaryEntity, cancellationToken);
            if (aggregateField == null)
            {
                return null;
            }

            var matchedFunction = "";
            foreach (var agg in aggregateField.Value.Field.Aggregations)
            {
                var normalizedAgg = _catalogService.NormalizeAggregationType(agg);
                if (ContainsPhrase(normalizedQuestion, agg) || ContainsPhrase(normalizedQuestion, normalizedAgg))
                {
                    matchedFunction = normalizedAgg;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(matchedFunction))
            {
                matchedFunction = "count"; // Default if the field is matched but no specific agg verb is found
            }

            return new CopilotDataAggregationPlan
            {
                Function = matchedFunction,
                Entity = aggregateField.Value.Entity.Name,
                Field = aggregateField.Value.Field.Name,
                Alias = $"{aggregateField.Value.Field.Name}{matchedFunction}"
            };
        }

        private async Task<(CopilotEntityDefinition Entity, CopilotFieldDefinition Field)?> ResolveAggregateFieldAsync(
            string normalizedQuestion,
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entity in EnumerateReachableEntities(catalog, primaryEntity))
            {
                foreach (var field in entity.Fields.Where(field => field.Aggregations.Count > 0))
                {
                    if (ContainsFieldReference(normalizedQuestion, field))
                    {
                        return (entity, field);
                    }
                }
            }

            return null;
        }

        private async Task ApplyMetadataFiltersAsync(
            CopilotDataIntentPlan plan,
            string normalizedQuestion,
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchedValues = await ResolveMatchedLookupValuesAsync(catalog, cancellationToken);
            var acceptedRanges = new List<(int Start, int End)>();
            var candidates = matchedValues
                .Select(match => new
                {
                    Match = match,
                    Phrase = CopilotLexicalMatcherService.Normalize(match.Value),
                    Position = FindPhrasePosition(normalizedQuestion, CopilotLexicalMatcherService.Normalize(match.Value))
                })
                .Where(item => item.Position >= 0)
                .OrderByDescending(item => item.Phrase.Length)
                .ThenBy(item => item.Position)
                .ToList();

            foreach (var candidate in candidates)
            {
                var start = candidate.Position;
                var end = candidate.Position + candidate.Phrase.Length - 1;
                if (acceptedRanges.Any(range => RangesOverlap(range.Start, range.End, start, end)))
                {
                    continue;
                }

                acceptedRanges.Add((start, end));
                AddEntityReference(plan, catalog, primaryEntity, candidate.Match.Entity.Name);
                AddDeterministicFilter(plan, candidate.Match.Entity.Name, candidate.Match.Field.Name, "equals", candidate.Match.Value);
                // Pathfinding is now integrated into AddEntityReference
            }

            ApplyBooleanFilters(plan, normalizedQuestion, primaryEntity);
            ApplyTemporalFilters(plan, normalizedQuestion, primaryEntity);
        }

        private void ApplyBooleanFilters(
            CopilotDataIntentPlan plan,
            string normalizedQuestion,
            CopilotEntityDefinition primaryEntity)
        {
            var activeField = FindField(primaryEntity, "IsActive");
            if (activeField != null)
            {
                if (ContainsPhrase(normalizedQuestion, "active"))
                {
                    AddDeterministicFilter(plan, primaryEntity.Name, activeField.Name, "equals", true);
                }
                else if (ContainsPhrase(normalizedQuestion, "inactive"))
                {
                    AddDeterministicFilter(plan, primaryEntity.Name, activeField.Name, "equals", false);
                }
            }
        }

        private void ApplyTemporalFilters(
            CopilotDataIntentPlan plan,
            string normalizedQuestion,
            CopilotEntityDefinition primaryEntity)
        {
            var dateField = primaryEntity.Fields.FirstOrDefault(f => f.Name.Equals("CreatedAt", StringComparison.OrdinalIgnoreCase));
            if (dateField == null) return;

            var todayMatch = Regex.Match(normalizedQuestion, @"\btoday\b", RegexOptions.IgnoreCase);
            if (todayMatch.Success)
            {
                var startOfDay = DateTime.UtcNow.Date;
                var endOfDay = startOfDay.AddDays(1).AddTicks(-1);
                AddDeterministicFilter(plan, primaryEntity.Name, dateField.Name, "between", $"{startOfDay:O}|{endOfDay:O}");
                return;
            }

            var daysMatch = Regex.Match(normalizedQuestion, @"\blast (\d+) days\b", RegexOptions.IgnoreCase);
            if (daysMatch.Success && int.TryParse(daysMatch.Groups[1].Value, out var days))
            {
                var start = DateTime.UtcNow.Date.AddDays(-days);
                var end = DateTime.UtcNow.Date.AddDays(1).AddTicks(-1);
                AddDeterministicFilter(plan, primaryEntity.Name, dateField.Name, "between", $"{start:O}|{end:O}");
                return;
            }

            var thisWeekMatch = Regex.Match(normalizedQuestion, @"\bthis week\b", RegexOptions.IgnoreCase);
            if (thisWeekMatch.Success)
            {
                var start = DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek);
                var end = DateTime.UtcNow.Date.AddDays(1).AddTicks(-1);
                AddDeterministicFilter(plan, primaryEntity.Name, dateField.Name, "between", $"{start:O}|{end:O}");
                return;
            }
            
            var thisMonthMatch = Regex.Match(normalizedQuestion, @"\bthis month\b", RegexOptions.IgnoreCase);
            if (thisMonthMatch.Success)
            {
                var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
                var end = DateTime.UtcNow.Date.AddDays(1).AddTicks(-1);
                AddDeterministicFilter(plan, primaryEntity.Name, dateField.Name, "between", $"{start:O}|{end:O}");
                return;
            }
        }

        private void EnsureDefaultAggregation(CopilotDataIntentPlan plan, CopilotEntityDefinition primaryEntity)
        {
            if (plan.Operation is not "count" and not "breakdown" || plan.Aggregations.Count > 0)
            {
                return;
            }

            var keyField = primaryEntity.Fields.FirstOrDefault(field => field.IsKey) ?? primaryEntity.Fields.FirstOrDefault();
            if (keyField == null)
            {
                return;
            }

            plan.Aggregations.Add(new CopilotDataAggregationPlan
            {
                Function = "count",
                Entity = primaryEntity.Name,
                Field = keyField.Name,
                Alias = "TotalCount"
            });
        }

        private void EnsureDefaultFields(CopilotDataIntentPlan plan, CopilotDataCatalog catalog, CopilotEntityDefinition primaryEntity)
        {
            if (plan.Fields.Count > 0 || plan.GroupBy.Count > 0 || plan.Aggregations.Count > 0)
            {
                return;
            }

            plan.Fields = BuildDefaultFieldRefs(catalog, primaryEntity);
        }

        private async Task<CopilotDataIntentPlan> ValidateAndNormalizeAsync(
            CopilotDataIntentPlan plan,
            CopilotDataCatalog catalog,
            string originalQuestion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messages = plan.ValidationMessages;
            var normalizedQuestion = originalQuestion.ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(plan.PrimaryEntity))
            {
                plan.PrimaryEntity = plan.Entities.FirstOrDefault() ?? "";
            }

            var primaryEntity = FindEntity(catalog, plan.PrimaryEntity);
            if (primaryEntity == null)
            {
                plan.RequiresClarification = true;
                plan.ClarificationQuestion = "Which approved data area should I query?";
                messages.Add($"Unknown primary entity '{plan.PrimaryEntity}'.");
                return plan;
            }

            plan.PrimaryEntity = primaryEntity.Name;
            plan.Operation = string.IsNullOrWhiteSpace(plan.Operation) ? "list" : plan.Operation.ToLowerInvariant();
            plan.OutputShape = string.IsNullOrWhiteSpace(plan.OutputShape) ? "table" : plan.OutputShape.ToLowerInvariant();
            
            plan.Entities = plan.Entities
                .Append(primaryEntity.Name)
                .Concat(ExtractEntityRefs(plan.Fields))
                .Concat(ExtractEntityRefs(plan.GroupBy))
                .Concat(plan.Filters.Select(filter => filter.Entity))
                .Concat(plan.Sorts.Select(sort => sort.Entity))
                .Concat(plan.Aggregations.Select(aggregation => aggregation.Entity))
                .Select(entity => FindEntity(catalog, entity)?.Name)
                .Where(entity => !string.IsNullOrWhiteSpace(entity))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(entity => entity!)
                .ToList();

            // W-2: Automatically resolve missing join paths for all involved entities.
            foreach (var entityName in plan.Entities.Where(e => !e.Equals(primaryEntity.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var path = FindJoinPath(catalog, primaryEntity, entityName);
                if (path != null)
                {
                    foreach (var step in path)
                    {
                        if (!plan.Joins.Any(j => j.FromEntity.Equals(step.FromEntity, StringComparison.OrdinalIgnoreCase) && 
                                                 j.ToEntity.Equals(step.ToEntity, StringComparison.OrdinalIgnoreCase)))
                        {
                            plan.Joins.Add(step);
                        }
                    }
                }
            }

            if (plan.Fields.Count == 0 && plan.GroupBy.Count == 0 && plan.Aggregations.Count == 0)
            {
                plan.Fields = BuildDefaultFieldRefs(catalog, primaryEntity);
            }

            plan.Fields = NormalizeFields(catalog, primaryEntity, plan.Fields, messages);
            plan.GroupBy = NormalizeFields(catalog, primaryEntity, plan.GroupBy, messages);
            NormalizeFilters(catalog, primaryEntity, plan.Filters, messages);
            NormalizeSorts(catalog, primaryEntity, plan.Sorts, plan.Aggregations, messages);
            NormalizeAggregations(catalog, primaryEntity, plan.Operation, plan.Aggregations, messages);
            await NormalizeJoinsAsync(plan, cancellationToken);

            var maxLimit = primaryEntity.MaxLimit <= 0 ? 100 : primaryEntity.MaxLimit;
            var defaultLimit = primaryEntity.DefaultLimit <= 0 ? 10 : primaryEntity.DefaultLimit;
            plan.Limit = plan.Limit.HasValue 
                ? Math.Clamp(plan.Limit.Value, 1, maxLimit)
                : defaultLimit;

            if (string.IsNullOrWhiteSpace(plan.PrimaryEntity) && messages.Count > 0)
            {
                plan.RequiresClarification = true;
                plan.ClarificationQuestion = "I could not safely determine which data area you want to query. Please clarify.";
            }

            return plan;
        }

        private List<string> NormalizeFields(
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity,
            IEnumerable<string> fields,
            List<string> messages)
        {
            var normalized = new List<string>();
            foreach (var fieldRef in fields)
            {
                var (entityName, fieldName) = SplitFieldRef(fieldRef, primaryEntity.Name);
                var entity = FindEntityExact(catalog, entityName);
                if (entity == null)
                {
                    messages.Add($"Unknown entity '{entityName}' in field '{fieldRef}'.");
                    continue;
                }

                var field = FindFieldExact(entity, fieldName);
                if (field == null || field.IsSensitive)
                {
                    messages.Add($"Unknown or restricted field '{fieldRef}'.");
                    continue;
                }

                normalized.Add($"{entity.Name}.{field.Name}");
            }

            return normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void NormalizeFilters(
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity,
            List<CopilotDataFilterPlan> filters,
            List<string> messages)
        {
            foreach (var filter in filters.ToList())
            {
                var entity = FindEntityExact(catalog, string.IsNullOrWhiteSpace(filter.Entity) ? primaryEntity.Name : filter.Entity);
                if (entity == null)
                {
                    messages.Add($"Unknown entity '{filter.Entity}' in filter.");
                    filters.Remove(filter);
                    continue;
                }

                var field = FindFieldExact(entity, filter.Field);
                if (field == null || field.IsSensitive || !field.Operators.Contains(filter.Operator, StringComparer.OrdinalIgnoreCase))
                {
                    messages.Add($"Unsupported filter '{entity.Name}.{filter.Field} {filter.Operator}'.");
                    filters.Remove(filter);
                    continue;
                }

                filter.Entity = entity.Name;
                filter.Field = field.Name;
            }
        }

        private void NormalizeSorts(
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity,
            List<CopilotDataSortPlan> sorts,
            IReadOnlyCollection<CopilotDataAggregationPlan> aggregations,
            List<string> messages)
        {
            foreach (var sort in sorts.ToList())
            {
                if (aggregations.Any(aggregation =>
                        aggregation.Alias.Equals(sort.Field, StringComparison.OrdinalIgnoreCase)))
                {
                    sort.Entity = "";
                    continue;
                }

                var entity = FindEntityExact(catalog, string.IsNullOrWhiteSpace(sort.Entity) ? primaryEntity.Name : sort.Entity);
                var field = entity == null ? null : FindFieldExact(entity, sort.Field);
                if (entity == null || field == null || field.IsSensitive)
                {
                    messages.Add($"Unsupported sort '{sort.Entity}.{sort.Field}'.");
                    sorts.Remove(sort);
                    continue;
                }

                sort.Entity = entity.Name;
                sort.Field = field.Name;
            }
        }

        private void NormalizeAggregations(
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity,
            string operation,
            List<CopilotDataAggregationPlan> aggregations,
            List<string> messages)
        {
            foreach (var aggregation in aggregations.ToList())
            {
                var entity = FindEntityExact(catalog, string.IsNullOrWhiteSpace(aggregation.Entity) ? primaryEntity.Name : aggregation.Entity);
                var field = entity == null ? null : FindFieldExact(entity, string.IsNullOrWhiteSpace(aggregation.Field) ? "Id" : aggregation.Field);

                if (entity == null || field == null || field.IsSensitive)
                {
                    messages.Add($"Unsupported aggregation '{aggregation.Function}' on '{aggregation.Entity}.{aggregation.Field}'.");
                    aggregations.Remove(aggregation);
                    continue;
                }

                var isCount = aggregation.Function.Equals("count", StringComparison.OrdinalIgnoreCase);
                if (!isCount && !field.Aggregations.Contains(aggregation.Function, StringComparer.OrdinalIgnoreCase))
                {
                    messages.Add($"Unsupported aggregation '{aggregation.Function}' on '{aggregation.Entity}.{aggregation.Field}'.");
                    aggregations.Remove(aggregation);
                    continue;
                }

                aggregation.Entity = entity.Name;
                aggregation.Field = field.Name;
                // W-2: Use the centralized normalization from the catalog service.
                aggregation.Function = _catalogService.NormalizeAggregationType(aggregation.Function);
                aggregation.Alias = string.IsNullOrWhiteSpace(aggregation.Alias) ? aggregation.Function : aggregation.Alias;
            }
        }

        private async Task NormalizeJoinsAsync(CopilotDataIntentPlan plan, CancellationToken cancellationToken)
        {
            foreach (var entity in plan.Entities.Where(entity => !entity.Equals(plan.PrimaryEntity, StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (plan.Joins.Any(join => join.ToEntity.Equals(entity, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var path = await _catalogService.ResolveJoinPathAsync(plan.PrimaryEntity, entity);
                if (path == null)
                {
                    plan.ValidationMessages.Add($"No approved join path from '{plan.PrimaryEntity}' to '{entity}'.");
                    continue;
                }

                var fromEntity = plan.PrimaryEntity;
                foreach (var relationship in path.Relationships)
                {
                    plan.Joins.Add(new CopilotDataJoinPlan
                    {
                        FromEntity = fromEntity,
                        ToEntity = relationship.Target,
                        Relationship = relationship.Name
                    });
                    fromEntity = relationship.Target;
                }
            }
        }

        private async Task<List<DeterministicLookupValueMatch>> ResolveMatchedLookupValuesAsync(
            CopilotDataCatalog catalog,
            CancellationToken cancellationToken)
        {
            var matches = new List<DeterministicLookupValueMatch>();
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            foreach (var entity in catalog.Entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entity.LookupEnrichment?.Enabled == true)
                {
                    var lookupValues = await LoadLookupValuesAsync(context, entity, entity.LookupEnrichment, cancellationToken);
                    foreach (var value in lookupValues)
                    {
                        var field = FindField(entity, entity.LookupEnrichment.ValueField);
                        if (field != null)
                        {
                            matches.Add(new DeterministicLookupValueMatch(entity, field, value));
                        }
                    }

                    continue;
                }

                if (!entity.Name.Equals("User", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var fieldName in new[] { "FirstName", "LastName", "Email" })
                {
                    var field = FindField(entity, fieldName);
                    if (field == null)
                    {
                        continue;
                    }

                    var values = await LoadDistinctFieldValuesAsync(context, entity, field, 250, cancellationToken);
                    // W-2: Prefer specific entities over generic user names by adding them to the start of the list or tracking priority.
                    matches.AddRange(values.Select(value => new DeterministicLookupValueMatch(entity, field, value)));
                }
            }

            // W-2: Prioritize matches by length and then by entity priority (Roles > Users).
            return matches
                .OrderByDescending(m => m.Value.Length)
                .ThenBy(m => m.Entity.Name.Equals("Role", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
        }

        private async Task<string?> ResolveFieldReferenceAsync(
            CopilotEntityDefinition primaryEntity,
            CopilotDataCatalog catalog,
            string phrase,
            string preferredCapability,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entity in EnumerateReachableEntities(catalog, primaryEntity))
            {
                var nameField = entity.Fields.FirstOrDefault(field =>
                    field.Name.Equals("Name", StringComparison.OrdinalIgnoreCase) &&
                    field.Capabilities.Contains(preferredCapability, StringComparer.OrdinalIgnoreCase));
                if (nameField != null && EnumerateEntityTerms(entity).Any(term => ContainsPhrase(phrase, term)))
                {
                    return $"{entity.Name}.{nameField.Name}";
                }

                foreach (var field in entity.Fields.Where(field =>
                             field.Capabilities.Contains(preferredCapability, StringComparer.OrdinalIgnoreCase)))
                {
                    if (!ContainsFieldReference(phrase, field))
                    {
                        continue;
                    }

                    return $"{entity.Name}.{field.Name}";
                }
            }

            return null;
        }

        private IEnumerable<CopilotEntityDefinition> EnumerateReachableEntities(
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity)
        {
            var queue = new Queue<(CopilotEntityDefinition Entity, int Depth)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue((primaryEntity, 0));
            visited.Add(primaryEntity.Name);

            while (queue.Count > 0)
            {
                var (entity, depth) = queue.Dequeue();
                yield return entity;

                if (depth >= 2)
                {
                    continue;
                }

                foreach (var relationship in entity.Relationships)
                {
                    var target = FindEntity(catalog, relationship.Target);
                    if (target == null || !visited.Add(target.Name))
                    {
                        continue;
                    }

                    queue.Enqueue((target, depth + 1));
                }
            }
        }

        private void AddRelationshipPreference(
            CopilotDataIntentPlan plan,
            CopilotEntityDefinition primaryEntity,
            CopilotEntityDefinition targetEntity,
            string normalizedQuestion)
        {
            if (primaryEntity.Name.Equals(targetEntity.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var directRelationships = primaryEntity.Relationships
                .Where(relationship => relationship.Target.Equals(targetEntity.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (directRelationships.Count == 0)
            {
                return;
            }

            if (directRelationships.Count == 1)
            {
                AddJoin(plan, primaryEntity.Name, targetEntity.Name, directRelationships[0].Name);
                return;
            }

            var preferred = ResolvePreferredRelationship(primaryEntity, targetEntity, directRelationships, normalizedQuestion)
                ?? directRelationships.FirstOrDefault();
            if (preferred != null)
            {
                AddJoin(plan, primaryEntity.Name, targetEntity.Name, preferred.Name);
            }
        }

        private CopilotRelationshipDefinition? ResolvePreferredRelationship(
            CopilotEntityDefinition sourceEntity,
            CopilotEntityDefinition targetEntity,
            IReadOnlyCollection<CopilotRelationshipDefinition> candidates,
            string normalizedQuestion)
        {
            return candidates
                .Select(relationship => new
                {
                    Relationship = relationship,
                    Score = ScoreRelationshipMatch(sourceEntity, relationship, targetEntity, normalizedQuestion)
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .Select(item => item.Relationship)
                .FirstOrDefault();
        }

        private int ScoreRelationshipMatch(
            CopilotEntityDefinition sourceEntity,
            CopilotRelationshipDefinition relationship,
            CopilotEntityDefinition targetEntity,
            string normalizedQuestion)
        {
            var score = 0;
            foreach (var term in EnumerateRelationshipTerms(sourceEntity, relationship, targetEntity))
            {
                if (ContainsPhrase(normalizedQuestion, term))
                {
                    score += Math.Max(term.Length, 2);
                }
            }

            return score;
        }

        private IEnumerable<string> EnumerateRelationshipTerms(
            CopilotEntityDefinition sourceEntity,
            CopilotRelationshipDefinition relationship,
            CopilotEntityDefinition targetEntity)
        {
            var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                CopilotLexicalMatcherService.Normalize(relationship.Name)
            };

            if (!string.IsNullOrWhiteSpace(relationship.Description))
            {
                terms.Add(CopilotLexicalMatcherService.Normalize(relationship.Description));
            }

            var sourceField = sourceEntity.Fields.FirstOrDefault(field =>
                field.Name.Equals(relationship.SourceField, StringComparison.OrdinalIgnoreCase));
            if (sourceField != null)
            {
                foreach (var alias in sourceField.Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)))
                {
                    terms.Add(CopilotLexicalMatcherService.Normalize(alias));
                }
            }

            foreach (var alias in targetEntity.Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)))
            {
                terms.Add(CopilotLexicalMatcherService.Normalize(alias));
            }

            return terms;
        }

        private static void AddJoin(CopilotDataIntentPlan plan, string fromEntity, string toEntity, string relationship)
        {
            if (plan.Joins.Any(join =>
                    join.FromEntity.Equals(fromEntity, StringComparison.OrdinalIgnoreCase) &&
                    join.ToEntity.Equals(toEntity, StringComparison.OrdinalIgnoreCase) &&
                    join.Relationship.Equals(relationship, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            plan.Joins.Add(new CopilotDataJoinPlan
            {
                FromEntity = fromEntity,
                ToEntity = toEntity,
                Relationship = relationship
            });
        }

        private void AddEntityReference(
            CopilotDataIntentPlan plan, 
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity,
            string entityName)
        {
            if (plan.Entities.Contains(entityName, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            plan.Entities.Add(entityName);

            if (primaryEntity.Name.Equals(entityName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // W-2: Resolve the join path from primary to the new entity.
            var path = FindJoinPath(catalog, primaryEntity, entityName);
            if (path == null)
            {
                return;
            }

            foreach (var step in path)
            {
                if (!plan.Joins.Any(j => j.FromEntity == step.FromEntity && j.ToEntity == step.ToEntity && j.Relationship == step.Relationship))
                {
                    plan.Joins.Add(step);
                    if (!plan.Entities.Contains(step.ToEntity, StringComparer.OrdinalIgnoreCase))
                    {
                        plan.Entities.Add(step.ToEntity);
                    }
                }
            }
        }

        private static List<CopilotDataJoinPlan>? FindJoinPath(
            CopilotDataCatalog catalog,
            CopilotEntityDefinition source,
            string targetName)
        {
            var queue = new Queue<(CopilotEntityDefinition Entity, List<CopilotDataJoinPlan> Path)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            queue.Enqueue((source, []));
            visited.Add(source.Name);

            while (queue.Count > 0)
            {
                var (current, path) = queue.Dequeue();

                if (current.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }

                if (path.Count >= 4) continue; // Safety limit (increased to 4 for deep paths)

                // W-2: Bi-directional search.
                // 1. Outgoing relationships
                foreach (var relationship in current.Relationships)
                {
                    var target = catalog.Entities.FirstOrDefault(e => e.Name.Equals(relationship.Target, StringComparison.OrdinalIgnoreCase));
                    if (target == null || visited.Contains(target.Name)) continue;

                    var newPath = new List<CopilotDataJoinPlan>(path)
                    {
                        new() { FromEntity = current.Name, ToEntity = target.Name, Relationship = relationship.Name }
                    };

                    visited.Add(target.Name);
                    queue.Enqueue((target, newPath));
                }

                // 2. Incoming relationships (reverse join)
                foreach (var otherEntity in catalog.Entities)
                {
                    foreach (var relationship in otherEntity.Relationships)
                    {
                        if (relationship.Target.Equals(current.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            if (visited.Contains(otherEntity.Name)) continue;

                            var newPath = new List<CopilotDataJoinPlan>(path)
                            {
                                new() { FromEntity = otherEntity.Name, ToEntity = current.Name, Relationship = relationship.Name }
                            };

                            visited.Add(otherEntity.Name);
                            queue.Enqueue((otherEntity, newPath));
                        }
                    }
                }
            }

            return null;
        }

        private static void AddDeterministicFilter(
            CopilotDataIntentPlan plan,
            string entityName,
            string fieldName,
            string @operator,
            object value)
        {
            if (plan.Filters.Any(filter =>
                    filter.Entity.Equals(entityName, StringComparison.OrdinalIgnoreCase) &&
                    filter.Field.Equals(fieldName, StringComparison.OrdinalIgnoreCase) &&
                    Equals(filter.Value, value)))
            {
                return;
            }

            plan.Filters.Add(new CopilotDataFilterPlan
            {
                Entity = entityName,
                Field = fieldName,
                Operator = @operator,
                Value = value
            });
        }

        private async Task<List<string>> LoadDistinctFieldValuesAsync(
            ApplicationDbContext context,
            CopilotEntityDefinition entity,
            CopilotFieldDefinition field,
            int maxValues,
            CancellationToken cancellationToken)
        {
            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT DISTINCT TOP ({Math.Clamp(maxValues, 1, 500)}) CONVERT(nvarchar(4000), {QuoteIdentifier(field.Name)}) AS Value
                FROM {QuoteIdentifier(entity.Table)}
                WHERE {QuoteIdentifier(field.Name)} IS NOT NULL
                ORDER BY Value
                """;

            var values = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var value = reader.GetValue(0)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }

            return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool ContainsFieldReference(string normalizedQuestion, CopilotFieldDefinition field)
        {
            if (ContainsPhrase(normalizedQuestion, CopilotLexicalMatcherService.Normalize(field.Name)))
            {
                return true;
            }

            return field.Aliases.Any(alias => ContainsPhrase(normalizedQuestion, CopilotLexicalMatcherService.Normalize(alias)));
        }

        private static int ScoreEntityMatch(string normalizedQuestion, CopilotEntityDefinition entity)
        {
            var score = 0;
            foreach (var term in EnumerateEntityTerms(entity))
            {
                if (ContainsPhrase(normalizedQuestion, term))
                {
                    score += term.Length;
                }
            }

            return score;
        }

        private static int FindEntityMatchPosition(string normalizedQuestion, CopilotEntityDefinition entity)
        {
            return EnumerateEntityTerms(entity)
                .Select(term => normalizedQuestion.IndexOf(term, StringComparison.OrdinalIgnoreCase))
                .Where(index => index >= 0)
                .DefaultIfEmpty(int.MaxValue)
                .Min();
        }

        private static IEnumerable<string> EnumerateEntityTerms(CopilotEntityDefinition entity)
            => entity.Aliases
                .Append(entity.Name)
                .Append(entity.Table)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(CopilotLexicalMatcherService.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase);

        private static bool ContainsPhrase(string text, string phrase)
            => text.Contains($" {phrase} ", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith($"{phrase} ", StringComparison.OrdinalIgnoreCase) ||
               text.EndsWith($" {phrase}", StringComparison.OrdinalIgnoreCase) ||
               text.Equals(phrase, StringComparison.OrdinalIgnoreCase);

        private static int FindPhrasePosition(string text, string phrase)
            => string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(phrase)
                ? -1
                : text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);

        private static bool RangesOverlap(int startA, int endA, int startB, int endB)
            => startA <= endB && startB <= endA;

        private async Task<string> GetKnownValuesAsync(CopilotDataCatalog catalog, CancellationToken cancellationToken)
        {
            var results = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            foreach (var entity in catalog.Entities.Where(e => e.LookupEnrichment?.Enabled == true))
            {
                var values = await LoadLookupValuesAsync(context, entity, entity.LookupEnrichment!, cancellationToken);
                if (values.Count > 0)
                {
                    results[entity.Name] = values;
                }
            }

            return JsonSerializer.Serialize(results, JsonOptions);
        }

        // Text scoring heuristics removed in favor of pure AI intent mapping.

        private static IEnumerable<string> ExtractEntityRefs(IEnumerable<string> fieldRefs)
            => fieldRefs
                .Select(field => (field ?? "").Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .Select(parts => parts[0]);

        private List<string> BuildDefaultFieldRefs(CopilotDataCatalog catalog, CopilotEntityDefinition primaryEntity)
        {
            var resolved = new List<string>();

            foreach (var defaultField in primaryEntity.DefaultFields)
            {
                var direct = FindFieldExact(primaryEntity, defaultField);
                if (direct != null && !direct.IsSensitive && direct.Capabilities.Contains("display", StringComparer.OrdinalIgnoreCase))
                {
                    resolved.Add($"{primaryEntity.Name}.{direct.Name}");
                    continue;
                }

                resolved.AddRange(ResolveDisplayProjectionFieldRefs(catalog, primaryEntity, defaultField));
            }

            if (resolved.Count == 0)
            {
                resolved.AddRange(primaryEntity.Fields
                    .Where(field => field.IsDefaultVisible && !field.IsSensitive && field.Capabilities.Contains("display", StringComparer.OrdinalIgnoreCase))
                    .Select(field => $"{primaryEntity.Name}.{field.Name}"));
            }

            return resolved
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private IEnumerable<string> ResolveDisplayProjectionFieldRefs(
            CopilotDataCatalog catalog,
            CopilotEntityDefinition primaryEntity,
            string defaultField)
        {
            foreach (var relationship in primaryEntity.Relationships.Where(relationship => relationship.IsDefaultJoin))
            {
                var relationshipDisplayNames = new[]
                {
                    relationship.Name,
                    $"{relationship.Name}Name",
                    $"{relationship.Name}Display"
                };

                if (!relationshipDisplayNames.Contains(defaultField, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = FindEntityExact(catalog, relationship.Target);
                if (target == null)
                {
                    continue;
                }

                var nameField = target.Fields.FirstOrDefault(field =>
                    field.Name.Equals("Name", StringComparison.OrdinalIgnoreCase) &&
                    !field.IsSensitive &&
                    field.Capabilities.Contains("display", StringComparer.OrdinalIgnoreCase));
                if (nameField != null)
                {
                    yield return $"{target.Name}.{nameField.Name}";
                    yield break;
                }

                foreach (var displayField in target.Fields.Where(field =>
                             (field.Name.Equals("FirstName", StringComparison.OrdinalIgnoreCase) ||
                              field.Name.Equals("LastName", StringComparison.OrdinalIgnoreCase) ||
                              field.Name.Equals("UserName", StringComparison.OrdinalIgnoreCase)) &&
                             !field.IsSensitive &&
                             field.Capabilities.Contains("display", StringComparer.OrdinalIgnoreCase)))
                {
                    yield return $"{target.Name}.{displayField.Name}";
                }

                yield break;
            }
        }

        private static (string Entity, string Field) SplitFieldRef(string fieldRef, string fallbackEntity)
        {
            var parts = (fieldRef ?? "").Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 2 ? (parts[0], parts[1]) : (fallbackEntity, fieldRef ?? "");
        }

        private CopilotEntityDefinition? FindEntityExact(CopilotDataCatalog catalog, string entityName)
            => catalog.Entities.FirstOrDefault(entity =>
                entity.Name.Equals(entityName, StringComparison.OrdinalIgnoreCase) ||
                entity.Table.Equals(entityName, StringComparison.OrdinalIgnoreCase) ||
                entity.Aliases.Any(alias => alias.Equals(entityName, StringComparison.OrdinalIgnoreCase)));

        private CopilotEntityDefinition? FindEntity(CopilotDataCatalog catalog, string entityName)
        {
            var match = FindEntityExact(catalog, entityName);

            if (match != null) return match;

            // Fuzzy fallback for schema resilience
            var candidates = catalog.Entities.Select(e => e.Name).Concat(catalog.Entities.Select(e => e.Table)).Distinct().ToList();
            var bestName = _lexicalMatcher.FindBestValue(entityName.ToLowerInvariant(), candidates);
            return string.IsNullOrWhiteSpace(bestName) ? null : catalog.Entities.FirstOrDefault(e => e.Name == bestName || e.Table == bestName);
        }

        private CopilotFieldDefinition? FindFieldExact(CopilotEntityDefinition entity, string fieldName)
            => entity.Fields.FirstOrDefault(field =>
                field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) ||
                field.Aliases.Any(alias => alias.Equals(fieldName, StringComparison.OrdinalIgnoreCase)));

        private CopilotFieldDefinition? FindField(CopilotEntityDefinition entity, string fieldName)
        {
            var match = FindFieldExact(entity, fieldName);

            if (match != null) return match;

            // Fuzzy fallback for schema resilience
            var candidates = entity.Fields.Select(f => f.Name).Concat(entity.Fields.SelectMany(f => f.Aliases)).Distinct().ToList();
            var bestName = _lexicalMatcher.FindBestValue(fieldName.ToLowerInvariant(), candidates);
            return string.IsNullOrWhiteSpace(bestName) ? null : entity.Fields.FirstOrDefault(f => f.Name == bestName || f.Aliases.Contains(bestName));
        }

        private static async Task<List<string>> LoadLookupValuesAsync(
            ApplicationDbContext context,
            CopilotEntityDefinition entity,
            CopilotLookupEnrichmentDefinition lookup,
            CancellationToken cancellationToken)
        {
            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = BuildLookupSql(entity, lookup);

            var values = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var value = reader.GetValue(0)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }

            return values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildLookupSql(CopilotEntityDefinition entity, CopilotLookupEnrichmentDefinition lookup)
        {
            var maxValues = Math.Clamp(lookup.MaxValues <= 0 ? 250 : lookup.MaxValues, 1, 1000);
            var valueField = QuoteIdentifier(lookup.ValueField);
            var predicates = new List<string>
            {
                $"{valueField} IS NOT NULL"
            };

            if (!string.IsNullOrWhiteSpace(lookup.ActiveField))
            {
                predicates.Add($"{QuoteIdentifier(lookup.ActiveField)} = 1");
            }

            return $"""
                SELECT DISTINCT TOP ({maxValues}) CONVERT(nvarchar(4000), {valueField}) AS Value
                FROM {QuoteIdentifier(entity.Table)}
                WHERE {string.Join(" AND ", predicates)}
                ORDER BY Value
                """;
        }

        private static string QuoteIdentifier(string identifier)
            => $"[{identifier.Replace("]", "]]")}]";

        private sealed record DeterministicLookupValueMatch(
            CopilotEntityDefinition Entity,
            CopilotFieldDefinition Field,
            string Value);
    }
}
