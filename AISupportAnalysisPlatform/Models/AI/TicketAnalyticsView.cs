namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Read-only model mapped to the vw_TicketAnalytics SQL View.
    /// Provides a fully denormalized, flat surface for the Copilot Dynamic Query engine.
    /// Every join is pre-resolved — the AI planner can reference any column directly.
    /// </summary>
    public class TicketAnalyticsView
    {
        // ── Ticket Core ──
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int DescriptionLength { get; set; }
        public int TitleLength { get; set; }

        // ── Lookups (pre-joined) ──
        public string StatusName { get; set; } = "";
        public bool IsClosedStatus { get; set; }
        public string PriorityName { get; set; } = "";
        public int PriorityLevel { get; set; }
        public string CategoryName { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string EntityName { get; set; } = "";

        // ── Technical / Environment ──
        public string? ProductArea { get; set; }
        public string? EnvironmentName { get; set; }
        public string? BrowserName { get; set; }
        public string? OperatingSystem { get; set; }
        public string? ImpactScope { get; set; }
        public int? AffectedUsersCount { get; set; }
        public string? EscalationLevel { get; set; }

        // ── People (pre-joined user names) ──
        public string CreatedByName { get; set; } = "";
        public string? AssignedToName { get; set; }
        public string? ResolvedByName { get; set; }
        public string? EscalatedToName { get; set; }

        // ── Dates ──
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? DueDate { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public DateTime? EscalatedAt { get; set; }
        public DateTime? FirstRespondedAt { get; set; }

        // ── SLA & Flags ──
        public bool IsSlaBreached { get; set; }
        public bool RequiresManagerReview { get; set; }

        // ── Resolution ──
        public string? ResolutionSummary { get; set; }
        public string? RootCause { get; set; }
        public string? PendingReason { get; set; }

        // ── Relationships ──
        public int? ParentTicketId { get; set; }

        // ── Aggregates (computed in the view) ──
        public int CommentCount { get; set; }
        public int AttachmentCount { get; set; }

        // ── Derived durations (computed in the view) ──
        public int? DaysOpen { get; set; }
        public int? HoursToFirstResponse { get; set; }
        public int? HoursToResolution { get; set; }
    }
}
