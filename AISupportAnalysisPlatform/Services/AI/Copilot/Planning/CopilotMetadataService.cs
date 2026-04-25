using AISupportAnalysisPlatform.Data;
using Microsoft.EntityFrameworkCore;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// W-5: Shared, per-request metadata cache.
    /// Both CopilotAnalyticsEngine and CopilotDataQuestionPlannerService previously
    /// issued 5-8 identical DB queries per analytics request. This service loads all
    /// metadata once per DI scope (once per request) and caches the result for the
    /// lifetime of that scope.
    /// </summary>
    public class CopilotMetadataService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private CopilotMetadataSnapshot? _snapshot;

        public CopilotMetadataService(IDbContextFactory<ApplicationDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        /// <summary>
        /// Returns live metadata loaded from the DB. After the first call within a
        /// request scope the result is cached in memory — subsequent callers pay zero DB cost.
        /// </summary>
        public async Task<CopilotMetadataSnapshot> GetAsync(CancellationToken cancellationToken = default)
        {
            if (_snapshot != null) return _snapshot;

            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var statusNames = await context.TicketStatuses.AsNoTracking()
                .Select(s => s.Name).OrderBy(n => n).ToListAsync(cancellationToken);

            var priorityNames = await context.TicketPriorities.AsNoTracking()
                .Select(p => p.Name).OrderBy(n => n).ToListAsync(cancellationToken);

            var entityNames = await context.Entities.AsNoTracking()
                .Where(e => e.IsActive).Select(e => e.Name).OrderBy(n => n).ToListAsync(cancellationToken);

            var categoryNames = await context.TicketAnalyticsView.AsNoTracking()
                .Where(t => !string.IsNullOrWhiteSpace(t.CategoryName))
                .Select(t => t.CategoryName).Distinct().OrderBy(n => n).ToListAsync(cancellationToken);

            var sourceNames = await context.TicketAnalyticsView.AsNoTracking()
                .Where(t => !string.IsNullOrWhiteSpace(t.SourceName))
                .Select(t => t.SourceName).Distinct().OrderBy(n => n).ToListAsync(cancellationToken);

            var productAreas = await context.TicketAnalyticsView.AsNoTracking()
                .Where(t => !string.IsNullOrWhiteSpace(t.ProductArea))
                .Select(t => t.ProductArea!).Distinct().OrderBy(n => n).ToListAsync(cancellationToken);

            var assignedToNames = await context.TicketAnalyticsView.AsNoTracking()
                .Where(t => !string.IsNullOrWhiteSpace(t.AssignedToName))
                .Select(t => t.AssignedToName!).Distinct().OrderBy(n => n).ToListAsync(cancellationToken);

            var createdByNames = await context.TicketAnalyticsView.AsNoTracking()
                .Where(t => !string.IsNullOrWhiteSpace(t.CreatedByName))
                .Select(t => t.CreatedByName).Distinct().OrderBy(n => n).ToListAsync(cancellationToken);

            _snapshot = new CopilotMetadataSnapshot(
                statusNames, priorityNames, entityNames,
                categoryNames, sourceNames, productAreas,
                assignedToNames, createdByNames);

            return _snapshot;
        }
    }

    /// <summary>
    /// Immutable snapshot of all live DB metadata needed by the analytics planning pipeline.
    /// </summary>
    public sealed record CopilotMetadataSnapshot(
        IReadOnlyList<string> StatusNames,
        IReadOnlyList<string> PriorityNames,
        IReadOnlyList<string> EntityNames,
        IReadOnlyList<string> CategoryNames,
        IReadOnlyList<string> SourceNames,
        IReadOnlyList<string> ProductAreas,
        IReadOnlyList<string> AssignedToNames,
        IReadOnlyList<string> CreatedByNames);
}
