namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Read-only model mapped to vw_EntityPerformance SQL View.
    /// Provides pre-aggregated entity-level KPIs for the Copilot.
    /// </summary>
    public class EntityPerformanceView
    {
        public int EntityId { get; set; }
        public string EntityName { get; set; } = "";
        public bool IsActive { get; set; }
        public int TotalTickets { get; set; }
        public int OpenTickets { get; set; }
        public int ClosedTickets { get; set; }
        public int ResolvedTickets { get; set; }
        public int SlaBreaches { get; set; }
        public decimal SlaBreachRate { get; set; }
        public int ManagerReviewCount { get; set; }
        public int? AvgResolutionHours { get; set; }
        public int? AvgFirstResponseHours { get; set; }
        public int EscalatedTickets { get; set; }
        public int TicketsLast7Days { get; set; }
        public int TicketsLast30Days { get; set; }
        public DateTime? LastTicketDate { get; set; }
        public DateTime? FirstTicketDate { get; set; }
    }
}
