namespace AISupportAnalysisPlatform.Models.AI
{
    public class AdminCopilotExecutionDetails
    {
        public string DetectedIntent { get; set; } = "";
        public string RouteReason { get; set; } = "";
        public RoutingConfidence PlannerConfidence { get; set; } = RoutingConfidence.Medium;
        public string SearchQuery { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? LastTechnicalData { get; set; } // NEW: Carrier for the latest handler's technical detail
        public int? ResultCount { get; set; }
        public List<CopilotExecutionStep> Steps { get; set; } = new();
        public AdminCopilotDynamicTicketQueryPlan? QueryPlan { get; set; } // The structured analytical plan
        public List<AdminCopilotDynamicTicketQueryPlan> QueryPlans { get; set; } = new();
        public AdminCopilotActionPlan? ActionPlan { get; set; } // The suggested platform action
        public long TotalElapsedMs { get; set; }

        public void AddStep(CopilotExecutionLayer layer, string action, string detail, CopilotStepStatus status = CopilotStepStatus.Ok, long elapsedMs = 0, string? technicalData = null)
        {
            Steps.Add(new CopilotExecutionStep
            {
                Layer = layer,
                Action = action,
                Detail = detail,
                Status = status,
                ElapsedMs = elapsedMs,
                TechnicalData = technicalData
            });
        }
    }

    public class CopilotExecutionStep
    {
        public CopilotExecutionLayer Layer { get; set; } = CopilotExecutionLayer.Context;
        public string Action { get; set; } = "";
        public string Detail { get; set; } = "";
        public string? TechnicalData { get; set; } // Stores Query SQL, API payloads, etc.
        public CopilotStepStatus Status { get; set; } = CopilotStepStatus.Ok;
        public long ElapsedMs { get; set; }
        public List<CopilotExecutionStep> SubSteps { get; set; } = new();

        public void AddSubStep(CopilotExecutionLayer layer, string action, string detail, CopilotStepStatus status = CopilotStepStatus.Ok, long elapsedMs = 0, string? technicalData = null)
        {
            SubSteps.Add(new CopilotExecutionStep
            {
                Layer = layer,
                Action = action,
                Detail = detail,
                Status = status,
                ElapsedMs = elapsedMs,
                TechnicalData = technicalData
            });
        }
    }

    public enum CopilotFilterState
    {
        Unspecified = 0,
        Only = 1,
        Clear = 2
    }

    public class AdminCopilotDynamicTicketQueryPlan
    {
        public string? TargetView { get; set; } // TicketRecords | EntitySummary
        public DynamicQueryIntent Intent { get; set; } = DynamicQueryIntent.Unspecified;
        public string Summary { get; set; } = "";
        public bool RequiresClarification { get; set; }
        public string ClarificationQuestion { get; set; } = "";
        public string TicketNumber { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string PriorityName { get; set; } = "";
        public string CategoryName { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string ProductArea { get; set; } = "";
        public string AssignedToName { get; set; } = "";
        public string CreatedByName { get; set; } = "";
        public List<string> StatusNames { get; set; } = new();
        public TicketDateRange RelativeDateRange { get; set; } = TicketDateRange.Any;
        public DateTime? AbsoluteStartDateUtc { get; set; }
        public DateTime? AbsoluteEndDateUtc { get; set; }
        public bool RequiresManagerReviewOnly { get; set; }
        public bool OpenOnly { get; set; }
        public bool ResolvedOnly { get; set; }
        public CopilotFilterState ManagerReviewFilterState { get; set; } = CopilotFilterState.Unspecified;
        public CopilotFilterState OpenFilterState { get; set; } = CopilotFilterState.Unspecified;
        public CopilotFilterState ResolvedFilterState { get; set; } = CopilotFilterState.Unspecified;
        public string TextSearch { get; set; } = "";
        public int MaxResults { get; set; } = 10;
        public string SortBy { get; set; } = "CreatedAt";
        public SortDirection SortDirection { get; set; } = SortDirection.Desc;
        public string? OrderByExpression { get; set; }
        public List<string> SelectedColumns { get; set; } = new();

        // ── GROUP BY support ──
        /// <summary>Column name from the ticket record data set to group by (e.g. "EntityName", "StatusName").</summary>
        public string? GroupByField { get; set; }
        /// <summary>Aggregation type: "count", "max", "min". Null defaults to count.</summary>
        public string? AggregationType { get; set; }
        /// <summary>Column to aggregate on (e.g. "DescriptionLength", "DaysOpen"). Used with max/min.</summary>
        public string? AggregationColumn { get; set; }

        /// <summary>
        /// NEW: Dynamic filters extracted from metadata catalog.
        /// Key = Canonical Field Name, Value = Parameter Value.
        /// This eliminates the 'Hardcoded Filter Wall'.
        /// </summary>
        public Dictionary<string, string> GlobalFilters { get; set; } = new();

        public bool HasExplicitTargetView { get; set; }
        public bool HasExplicitLimit { get; set; }
        public bool HasExplicitSort { get; set; }
        public bool HasExplicitColumns { get; set; }
        public bool HasExplicitGrouping { get; set; }
        public bool HasExplicitDateRange { get; set; }
        public bool HasExplicitManagerReviewFilter { get; set; }
        public bool HasExplicitOpenFilter { get; set; }
        public bool HasExplicitResolvedFilter { get; set; }
    }

    public class AdminCopilotDynamicTicketQueryExecution
    {
        public AdminCopilotDynamicTicketQueryPlan Plan { get; set; } = new();
        public int TotalCount { get; set; }
        public List<AdminCopilotStatusCount> StatusBreakdown { get; set; } = new();
        public List<AdminCopilotTicketQueryRow> Rows { get; set; } = new();
        public List<string> StructuredColumns { get; set; } = new();
        public List<AdminCopilotStructuredResultRow> StructuredRows { get; set; } = new();
        public string? GeneratedSql { get; set; } 
        public string Summary { get; set; } = "";
        public string Answer { get; set; } = "";
        public List<CopilotExecutionStep> ExecutionSteps { get; set; } = new();
    }



    public class AdminCopilotStructuredResultRow
    {
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? LinkUrl { get; set; }
    }

    public class AdminCopilotStatusCount
    {
        public string StatusName { get; set; } = "";
        public int Count { get; set; }
    }

    public class AdminCopilotTicketQueryRow
    {
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public string Priority { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string CategoryName { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string ProductArea { get; set; } = "";
        public string CreatedByName { get; set; } = "";
        public string AssignedToName { get; set; } = "";
        public bool RequiresManagerReview { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ResolvedAtUtc { get; set; }
    }

    public class AdminCopilotActionPlan
    {
        public PlatformActionIntent Intent { get; set; } = PlatformActionIntent.None;
        public int? TargetTicketId { get; set; }
        public string? TargetTicketNumber { get; set; }
        public string? TargetValue { get; set; } // UserId, StatusId, Level, or Comment Content
        public string? TargetValueDisplay { get; set; } // Readable name for UI
        public string Summary { get; set; } = "";
        public bool RequiresConfirmation { get; set; } = true;
        public bool IsExecuted { get; set; }
    }
}
