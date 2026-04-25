using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotFollowUpPlanMergerService
    {
        public CopilotFollowUpPlanMergerService()
        {
        }

        public AdminCopilotDynamicTicketQueryPlan Merge(
            AdminCopilotDynamicTicketQueryPlan current,
            CopilotQuestionContext questionContext)
        {
            var previous = questionContext.ConversationContext.PreviousQueryPlan;
            if (!questionContext.IsFollowUpQuestion || previous == null)
            {
                return current;
            }

            var merged = Clone(previous);
            var currentManagerReviewState = NormalizeLegacyFilterState(current.ManagerReviewFilterState, current.RequiresManagerReviewOnly);
            var previousManagerReviewState = NormalizeLegacyFilterState(previous.ManagerReviewFilterState, previous.RequiresManagerReviewOnly);
            var currentOpenState = NormalizeLegacyFilterState(current.OpenFilterState, current.OpenOnly);
            var previousOpenState = NormalizeLegacyFilterState(previous.OpenFilterState, previous.OpenOnly);
            var currentResolvedState = NormalizeLegacyFilterState(current.ResolvedFilterState, current.ResolvedOnly);
            var previousResolvedState = NormalizeLegacyFilterState(previous.ResolvedFilterState, previous.ResolvedOnly);
            var deltaOnlyRequest = IsDeltaOnlyRequest(current);
            var preservePreviousTargetView = deltaOnlyRequest &&
                string.Equals(current.TargetView, "TicketAnalytics", StringComparison.OrdinalIgnoreCase) &&
                !HasExplicitTicketLevelSignal(current) &&
                !string.Equals(previous.TargetView, "TicketAnalytics", StringComparison.OrdinalIgnoreCase);

            merged.TargetView = preservePreviousTargetView
                ? previous.TargetView
                : Prefer(current.TargetView, previous.TargetView);

            merged.Intent = current.Intent != DynamicQueryIntent.Unspecified
                ? current.Intent
                : previous.Intent != DynamicQueryIntent.Unspecified 
                    ? previous.Intent
                    : DynamicQueryIntent.List;
            merged.Summary = Prefer(current.Summary, previous.Summary);
            merged.RequiresClarification = current.RequiresClarification;
            merged.ClarificationQuestion = current.ClarificationQuestion;

            merged.TicketNumber = ShouldCarryTicketNumber(current, merged.TargetView)
                ? Prefer(current.TicketNumber, previous.TicketNumber)
                : current.TicketNumber;
            merged.EntityName = Prefer(current.EntityName, previous.EntityName);
            merged.PriorityName = ShouldUseCurrentValue(current.PriorityName)
                ? current.PriorityName
                : previous.PriorityName;
            merged.CategoryName = ShouldUseCurrentValue(current.CategoryName)
                ? current.CategoryName
                : previous.CategoryName;
            merged.SourceName = ShouldUseCurrentValue(current.SourceName)
                ? current.SourceName
                : previous.SourceName;
            merged.ProductArea = ShouldUseCurrentValue(current.ProductArea)
                ? current.ProductArea
                : previous.ProductArea;
            merged.AssignedToName = ShouldUseCurrentValue(current.AssignedToName)
                ? current.AssignedToName
                : previous.AssignedToName;
            merged.CreatedByName = ShouldUseCurrentValue(current.CreatedByName)
                ? current.CreatedByName
                : previous.CreatedByName;
            merged.StatusNames = current.StatusNames.Any()
                ? current.StatusNames.ToList()
                : previous.StatusNames.ToList();
            merged.RelativeDateRange = current.RelativeDateRange != TicketDateRange.Any
                ? current.RelativeDateRange
                : previous.RelativeDateRange;
            merged.AbsoluteStartDateUtc = current.HasExplicitDateRange
                ? current.AbsoluteStartDateUtc
                : previous.AbsoluteStartDateUtc;
            merged.AbsoluteEndDateUtc = current.HasExplicitDateRange
                ? current.AbsoluteEndDateUtc
                : previous.AbsoluteEndDateUtc;
            merged.ManagerReviewFilterState = MergeFilterState(
                currentManagerReviewState,
                previousManagerReviewState,
                current.HasExplicitManagerReviewFilter);
            merged.OpenFilterState = MergeFilterState(
                currentOpenState,
                previousOpenState,
                current.HasExplicitOpenFilter);
            merged.ResolvedFilterState = MergeFilterState(
                currentResolvedState,
                previousResolvedState,
                current.HasExplicitResolvedFilter);
            EnforceExclusiveStatusFilters(merged);
            merged.RequiresManagerReviewOnly = merged.ManagerReviewFilterState == CopilotFilterState.Only;
            merged.OpenOnly = merged.OpenFilterState == CopilotFilterState.Only;
            merged.ResolvedOnly = merged.ResolvedFilterState == CopilotFilterState.Only;
            merged.TextSearch = ShouldUseCurrentValue(current.TextSearch)
                ? current.TextSearch
                : previous.TextSearch;
            var resetLimitForBroadScope = ShouldResetLimitForBroadScope(current, questionContext);
            merged.MaxResults = current.HasExplicitLimit
                ? current.MaxResults
                    : resetLimitForBroadScope
                        ? current.MaxResults
                        : previous.MaxResults;
            merged.SortBy = current.HasExplicitSort
                ? current.SortBy
                : previous.SortBy;
            merged.SortDirection = current.HasExplicitSort
                ? current.SortDirection
                : previous.SortDirection;
            merged.OrderByExpression = ShouldUseCurrentValue(current.OrderByExpression)
                ? current.OrderByExpression
                : previous.OrderByExpression;
            merged.SelectedColumns = current.HasExplicitColumns
                ? current.SelectedColumns.ToList()
                : previous.SelectedColumns.ToList();
            var isGroupingIntent = merged.Intent == DynamicQueryIntent.GroupBy || merged.Intent == DynamicQueryIntent.Summarize;
            
            merged.GroupByField = current.HasExplicitGrouping
                ? current.GroupByField
                : isGroupingIntent ? previous.GroupByField : null;

            merged.AggregationType = current.HasExplicitGrouping
                ? current.AggregationType
                : isGroupingIntent ? previous.AggregationType : null;

            merged.AggregationColumn = current.HasExplicitGrouping
                ? current.AggregationColumn
                : isGroupingIntent ? previous.AggregationColumn : null;
            
            merged.HasExplicitGrouping = current.HasExplicitGrouping || (isGroupingIntent && previous.HasExplicitGrouping);
            merged.HasExplicitTargetView = current.HasExplicitTargetView || (!current.HasExplicitTargetView && previous.HasExplicitTargetView);
            merged.HasExplicitLimit = current.HasExplicitLimit || (!current.HasExplicitLimit && !resetLimitForBroadScope && previous.HasExplicitLimit);
            merged.HasExplicitSort = current.HasExplicitSort || (!current.HasExplicitSort && previous.HasExplicitSort);
            merged.HasExplicitColumns = current.HasExplicitColumns || (!current.HasExplicitColumns && previous.HasExplicitColumns);
            merged.HasExplicitDateRange = current.HasExplicitDateRange || (!current.HasExplicitDateRange && previous.HasExplicitDateRange);
            merged.HasExplicitResolvedFilter = merged.ResolvedFilterState != CopilotFilterState.Unspecified;

            // Global Filters Merge (A-01 Resolved)
            foreach (var filter in current.GlobalFilters)
            {
                merged.GlobalFilters[filter.Key] = filter.Value;
            }

            return merged;
        }


        private static bool IsDeltaOnlyRequest(AdminCopilotDynamicTicketQueryPlan current)
        {
            return !current.HasExplicitTargetView &&
                   !HasExplicitScopeChange(current) &&
                   (current.HasExplicitLimit ||
                    current.HasExplicitSort ||
                    current.HasExplicitColumns ||
                    current.HasExplicitDateRange);
        }

        private static bool HasExplicitScopeChange(AdminCopilotDynamicTicketQueryPlan current)
        {
            return HasExplicitTicketLevelSignal(current) ||
                   ShouldUseCurrentValue(current.EntityName) ||
                   ShouldUseCurrentValue(current.CategoryName) ||
                   ShouldUseCurrentValue(current.SourceName) ||
                   ShouldUseCurrentValue(current.AssignedToName) ||
                   ShouldUseCurrentValue(current.CreatedByName) ||
                   ShouldUseCurrentValue(current.TextSearch);
        }

        private static bool HasExplicitTicketLevelSignal(AdminCopilotDynamicTicketQueryPlan current)
        {
            return ShouldUseCurrentValue(current.TicketNumber) ||
                   ShouldUseCurrentValue(current.PriorityName) ||
                   ShouldUseCurrentValue(current.CategoryName) ||
                   ShouldUseCurrentValue(current.SourceName) ||
                   ShouldUseCurrentValue(current.ProductArea) ||
                   ShouldUseCurrentValue(current.AssignedToName) ||
                   ShouldUseCurrentValue(current.CreatedByName) ||
                   current.StatusNames.Any() ||
                   current.HasExplicitManagerReviewFilter ||
                   current.HasExplicitOpenFilter ||
                   current.HasExplicitResolvedFilter ||
                   current.HasExplicitGrouping;
        }

        private static bool HasExplicitIntentChange(AdminCopilotDynamicTicketQueryPlan current)
            => current.Intent != DynamicQueryIntent.Unspecified || current.HasExplicitGrouping;

        private static CopilotFilterState MergeFilterState(
            CopilotFilterState current,
            CopilotFilterState previous,
            bool hasExplicitChange)
            => hasExplicitChange ? current : previous;

        private static CopilotFilterState NormalizeLegacyFilterState(CopilotFilterState state, bool legacyValue)
            => state != CopilotFilterState.Unspecified
                ? state
                : legacyValue
                    ? CopilotFilterState.Only
                    : CopilotFilterState.Unspecified;

        private static void EnforceExclusiveStatusFilters(AdminCopilotDynamicTicketQueryPlan plan)
        {
            if (plan.OpenFilterState == CopilotFilterState.Only)
            {
                plan.ResolvedFilterState = CopilotFilterState.Clear;
            }
            else if (plan.ResolvedFilterState == CopilotFilterState.Only)
            {
                plan.OpenFilterState = CopilotFilterState.Clear;
            }
        }

        private bool ShouldResetLimitForBroadScope(AdminCopilotDynamicTicketQueryPlan current, CopilotQuestionContext questionContext)
        {
            var normalized = questionContext.OriginalQuestion.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            var broadEntitySignals = new[] { "each entity", "for each entity", "per entity", "by entity", "all entities", "show entities", "compare entities" };
            var entityWideRequest =
                string.Equals(current.TargetView, "EntityPerformance", StringComparison.OrdinalIgnoreCase) &&
                ContainsAny(normalized, broadEntitySignals);

            return entityWideRequest;
        }

        private static bool ShouldCarryTicketNumber(AdminCopilotDynamicTicketQueryPlan current, string? targetView)
            => string.Equals(targetView, "TicketAnalytics", StringComparison.OrdinalIgnoreCase) ||
               ShouldUseCurrentValue(current.TicketNumber);

        private static bool ShouldUseCurrentValue(string? value)
            => !string.IsNullOrWhiteSpace(value);

        private static string Prefer(string? current, string? fallback)
            => string.IsNullOrWhiteSpace(current) ? fallback ?? "" : current;

        private static bool ContainsAny(string input, IEnumerable<string> values)
            => values.Any(value => input.Contains(value, StringComparison.OrdinalIgnoreCase));

        private static AdminCopilotDynamicTicketQueryPlan Clone(AdminCopilotDynamicTicketQueryPlan source)
        {
            return new AdminCopilotDynamicTicketQueryPlan
            {
                TargetView = source.TargetView,
                Intent = source.Intent,
                Summary = source.Summary,
                RequiresClarification = source.RequiresClarification,
                ClarificationQuestion = source.ClarificationQuestion,
                TicketNumber = source.TicketNumber,
                EntityName = source.EntityName,
                PriorityName = source.PriorityName,
                CategoryName = source.CategoryName,
                SourceName = source.SourceName,
                ProductArea = source.ProductArea,
                AssignedToName = source.AssignedToName,
                CreatedByName = source.CreatedByName,
                StatusNames = source.StatusNames.ToList(),
                RelativeDateRange = source.RelativeDateRange,
                AbsoluteStartDateUtc = source.AbsoluteStartDateUtc,
                AbsoluteEndDateUtc = source.AbsoluteEndDateUtc,
                RequiresManagerReviewOnly = source.RequiresManagerReviewOnly,
                OpenOnly = source.OpenOnly,
                ResolvedOnly = source.ResolvedOnly,
                ManagerReviewFilterState = source.ManagerReviewFilterState,
                OpenFilterState = source.OpenFilterState,
                ResolvedFilterState = source.ResolvedFilterState,
                TextSearch = source.TextSearch,
                MaxResults = source.MaxResults,
                SortBy = source.SortBy,
                SortDirection = source.SortDirection,
                OrderByExpression = source.OrderByExpression,
                SelectedColumns = source.SelectedColumns.ToList(),
                GroupByField = source.GroupByField,
                AggregationType = source.AggregationType,
                AggregationColumn = source.AggregationColumn,
                HasExplicitTargetView = source.HasExplicitTargetView,
                HasExplicitLimit = source.HasExplicitLimit,
                HasExplicitSort = source.HasExplicitSort,
                HasExplicitColumns = source.HasExplicitColumns,
                HasExplicitGrouping = source.HasExplicitGrouping,
                HasExplicitDateRange = source.HasExplicitDateRange,
                HasExplicitManagerReviewFilter = source.HasExplicitManagerReviewFilter,
                HasExplicitOpenFilter = source.HasExplicitOpenFilter,
                HasExplicitResolvedFilter = source.HasExplicitResolvedFilter,
                GlobalFilters = new Dictionary<string, string>(source.GlobalFilters)
            };
        }

    }
}
