using AISupportAnalysisPlatform.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Constants;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Rendering;
using AISupportAnalysisPlatform.Services.Infrastructure;
using AISupportAnalysisPlatform.Models.Common;
using AutoMapper;
using AutoMapper.QueryableExtensions;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class AiInsightsService : IAiInsightsService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly IAiReviewSignalService _signalService;
        private readonly ILocalizationService _localizer;
        private readonly IMapper _mapper;

        public AiInsightsService(IDbContextFactory<ApplicationDbContext> contextFactory, IAiReviewSignalService signalService, ILocalizationService localizer, IMapper mapper)
        {
            _contextFactory = contextFactory;
            _signalService = signalService;
            _localizer = localizer;
            _mapper = mapper;
        }

        private static (DateTime? StartDateUtc, DateTime? EndDateExclusiveUtc) NormalizeDateRange(AiInsightsFilter filters)
        {
            var startDate = filters.StartDate?.Date;
            var endDate = filters.EndDate?.Date;

            if (startDate.HasValue && endDate.HasValue && startDate > endDate)
            {
                (startDate, endDate) = (endDate, startDate);
            }

            return (startDate, endDate?.AddDays(1));
        }

        private static int GetAiPriorityLevel(string? suggestedPriority)
        {
            return suggestedPriority switch
            {
                TicketPriorityNames.Critical => 4,
                TicketPriorityNames.High => 3,
                TicketPriorityNames.Medium => 2,
                _ => 1
            };
        }

        private async Task<FilteredAnalysisData> GetFilteredDataAsync(AiInsightsFilter filters)
        {
            var (startDateUtc, endDateExclusiveUtc) = NormalizeDateRange(filters);

            using var context = await _contextFactory.CreateDbContextAsync();
            var query = context.TicketAiAnalyses.AsNoTracking().AsQueryable();

            if (startDateUtc.HasValue)
                query = query.Where(a => a.CreatedOn >= startDateUtc.Value);
            if (endDateExclusiveUtc.HasValue)
                query = query.Where(a => a.CreatedOn < endDateExclusiveUtc.Value);

            var runCounts = await query
                .GroupBy(a => a.TicketId)
                .Select(g => new { TicketId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TicketId, x => x.Count);

            var latestSuccessfulRuns = query
                .Where(a => a.AnalysisStatus == AiAnalysisStatus.Success)
                .GroupBy(a => a.TicketId)
                .Select(g => new
                {
                    TicketId = g.Key,
                    RunNumber = g.Max(x => x.RunNumber)
                });

            var snapshots = await query
                .Where(a => latestSuccessfulRuns.Any(l => l.TicketId == a.TicketId && l.RunNumber == a.RunNumber))
                .ProjectTo<AnalysisSnapshot>(_mapper.ConfigurationProvider)
                .ToListAsync();

            foreach (var s in snapshots)
            {
                s.RunCount = runCounts.GetValueOrDefault(s.TicketId, 1);
            }

            return new FilteredAnalysisData
            {
                LatestAnalyses = snapshots,
                RunCounts = runCounts
            };
        }

        private TicketAiAnalysis ToReviewSignalInput(AnalysisSnapshot analysis)
        {
            return new TicketAiAnalysis
            {
                ConfidenceLevel = analysis.ConfidenceLevel,
                SuggestedClassification = analysis.SuggestedClassification ?? "Unknown",
                SuggestedPriority = analysis.SuggestedPriority,
                Ticket = new Ticket
                {
                    TicketNumber = analysis.TicketNumber,
                    Title = analysis.Title,
                    Description = new string('x', Math.Max(analysis.DescriptionLength, 0)),
                    Category = new TicketCategory { Name = analysis.CurrentClassification ?? "None" },
                    Priority = new TicketPriority { Name = analysis.CurrentPriority ?? "Unknown", Level = analysis.CurrentPriorityLevel },
                    Attachments = Enumerable.Range(0, analysis.TicketAttachmentCount).Select(_ => new TicketAttachment()).ToList()
                }
            };
        }

        private bool NeedsReview(AnalysisSnapshot analysis)
        {
            return _signalService.NeedsReview(ToReviewSignalInput(analysis), analysis.RunCount, _localizer.CurrentLanguage);
        }

        private List<string> CalculateReviewReasons(AnalysisSnapshot analysis)
        {
            return _signalService.CalculateReviewReasons(ToReviewSignalInput(analysis), analysis.RunCount, _localizer.CurrentLanguage);
        }

        private bool IsPriorityEscalated(AnalysisSnapshot a)
        {
            var aiLevel = GetAiPriorityLevel(a.SuggestedPriority);
            return aiLevel > a.CurrentPriorityLevel;
        }

        private AiOverviewKpis BuildDashboardOverview(FilteredAnalysisData data)
        {
            return new AiOverviewKpis
            {
                TotalAnalyses = data.LatestAnalyses.Count,
                LowConfidence = data.LatestAnalyses.Count(a => a.ConfidenceLevel == AiConfidenceLevel.Low),
                UnknownClassification = data.LatestAnalyses.Count(a => (a.SuggestedClassification ?? "Unknown") == "Unknown"),
                ClassificationMismatch = data.LatestAnalyses.Count(a => a.SuggestedClassification != "Unknown" && a.SuggestedClassification != a.CurrentClassification),
                PriorityMismatch = data.LatestAnalyses.Count(IsPriorityEscalated),
                StrongAttachmentEvidence = 0, // Removed feature
                MultipleReRuns = data.LatestAnalyses.Count(a => a.RunCount > 1),
                NeedsReview = data.LatestAnalyses.Count(NeedsReview)
            };
        }

        private List<ReviewAttentionItem> BuildReviewAttentionItems(FilteredAnalysisData data)
        {
            var items = new List<ReviewAttentionItem>();

            foreach (var ai in data.LatestAnalyses)
            {
                var reasons = CalculateReviewReasons(ai);
                if (!reasons.Any()) continue;

                items.Add(new ReviewAttentionItem
                {
                    AnalysisId = ai.AnalysisId,
                    TicketId = ai.TicketId,
                    TicketNumber = ai.TicketNumber,
                    Title = ai.Title,
                    CurrentClassification = ai.CurrentClassification,
                    AiClassification = ai.SuggestedClassification,
                    CurrentPriority = ai.CurrentPriority,
                    AiPriority = ai.SuggestedPriority,
                    Confidence = ai.ConfidenceLevel.ToString(),
                    ReviewReasons = reasons,
                    HasAttachments = ai.TicketAttachmentCount > 0,
                    LastAiRun = ai.CreatedOn
                });
            }

            return items.OrderByDescending(item => item.LastAiRun).ToList();
        }

        private static ConfidenceInsights BuildConfidenceBreakdown(FilteredAnalysisData data)
        {
            return new ConfidenceInsights
            {
                High = data.LatestAnalyses.Count(a => a.ConfidenceLevel == AiConfidenceLevel.High),
                Medium = data.LatestAnalyses.Count(a => a.ConfidenceLevel == AiConfidenceLevel.Medium),
                Low = data.LatestAnalyses.Count(a => a.ConfidenceLevel == AiConfidenceLevel.Low)
            };
        }

        private AttachmentEvidenceInsights BuildAttachmentInsights(FilteredAnalysisData data)
        {
            return new AttachmentEvidenceInsights
            {
                TicketsWithAiAttachments = 0,
                TicketsWithHighRelevanceAttachments = 0,
                MostRelevantAttachments = new List<PatternInsight>()
            };
        }

        private List<TrendInsight> BuildTrendData(FilteredAnalysisData data)
        {
            return data.LatestAnalyses
                .GroupBy(a => a.CreatedOn.Date)
                .OrderBy(g => g.Key)
                .Select(g => new TrendInsight
                {
                    DateLabel = g.Key.ToString("MM/dd"),
                    AnalysisVolume = g.Count(),
                    NeedsReviewVolume = g.Count(NeedsReview)
                })
                .ToList();
        }

        private List<PatternInsight> BuildClassificationPatterns(FilteredAnalysisData data)
        {
            return data.LatestAnalyses
                .GroupBy(a => a.SuggestedClassification ?? "Unknown")
                .Select(g => new PatternInsight { Label = g.Key, Count = g.Count() })
                .OrderByDescending(p => p.Count)
                .Take(5)
                .ToList();
        }

        private List<PatternInsight> BuildPriorityPatterns(FilteredAnalysisData data)
        {
            return data.LatestAnalyses
                .GroupBy(a => a.SuggestedPriority ?? TicketPriorityNames.Medium)
                .Select(g => new PatternInsight { Label = g.Key, Count = g.Count() })
                .OrderByDescending(p => p.Count)
                .ToList();
        }

        public async Task<AiOverviewKpis> GetDashboardOverviewAsync(AiInsightsFilter filters)
        {
            var data = await GetFilteredDataAsync(filters);
            return BuildDashboardOverview(data);
        }

        public async Task<List<ReviewAttentionItem>> GetReviewAttentionItemsAsync(AiInsightsFilter filters)
        {
            var data = await GetFilteredDataAsync(filters);
            return BuildReviewAttentionItems(data);
        }

        public async Task<ConfidenceInsights> GetConfidenceBreakdownAsync(AiInsightsFilter filters)
        {
            var data = await GetFilteredDataAsync(filters);
            return BuildConfidenceBreakdown(data);
        }

        public async Task<AttachmentEvidenceInsights> GetAttachmentInsightsAsync(AiInsightsFilter filters)
        {
            var data = await GetFilteredDataAsync(filters);
            return BuildAttachmentInsights(data);
        }

        public async Task<List<TrendInsight>> GetTrendDataAsync(AiInsightsFilter filters)
        {
            var data = await GetFilteredDataAsync(filters);
            return BuildTrendData(data);
        }

        public async Task<List<PatternInsight>> GetClassificationPatternsAsync(AiInsightsFilter filters)
        {
            var data = await GetFilteredDataAsync(filters);
            return BuildClassificationPatterns(data);
        }

        public async Task<List<PatternInsight>> GetPriorityPatternsAsync(AiInsightsFilter filters)
        {
            var data = await GetFilteredDataAsync(filters);
            return BuildPriorityPatterns(data);
        }

        public async Task<AiInsightsDashboardViewModel> GetDashboardAsync(AiInsightsFilter filters)
        {
            filters.Normalize();
            var data = await GetFilteredDataAsync(filters);
            var reviewItems = BuildReviewAttentionItems(data);
            var totalReviewItems = reviewItems.Count;
            var effectivePageSize = filters.GetEffectivePageSize(totalReviewItems);
            var pagedReviewItems = reviewItems
                .Skip((filters.PageNumber - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .ToList();

            var vm = new AiInsightsDashboardViewModel
            {
                Filter = filters,
                Kpis = BuildDashboardOverview(data),
                ReviewItems = new PagedResult<ReviewAttentionItem>
                {
                    Items = pagedReviewItems,
                    TotalCount = totalReviewItems,
                    PageNumber = filters.PageNumber,
                    PageSize = effectivePageSize
                },
                ClassificationPatterns = BuildClassificationPatterns(data),
                PriorityPatterns = BuildPriorityPatterns(data),
                ConfidenceData = BuildConfidenceBreakdown(data),
                AttachmentInsights = BuildAttachmentInsights(data),
                VolumeTrends = BuildTrendData(data)
            };

            // Populate Dropdowns
            using (var context = await _contextFactory.CreateDbContextAsync())
            {
                var categories = await context.TicketCategories.AsNoTracking().Select(c => c.Name).Distinct().OrderBy(name => name).ToListAsync();
                var priorities = await context.TicketPriorities.AsNoTracking().OrderBy(p => p.Level).Select(p => p.Name).Distinct().ToListAsync();
                var aiClassifications = await context.TicketAiAnalyses.AsNoTracking().Select(a => a.SuggestedClassification).Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c).ToListAsync();
                var aiPriorities = await context.TicketAiAnalyses.AsNoTracking().Select(a => a.SuggestedPriority).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToListAsync();

                vm.ClassificationList = new SelectList(categories, filters.CurrentClassification);
                vm.PriorityList = new SelectList(priorities, filters.CurrentPriority);
                vm.AiClassificationList = new SelectList(aiClassifications, filters.AiSuggestedClassification);
                vm.AiPriorityList = new SelectList(aiPriorities, filters.AiSuggestedPriority);
            }

            return vm;
        }

        public async Task<bool> CompleteReviewAsync(int analysisId, string notes, string reviewer)
        {
            // Review feature removed from lightweight model
            return await Task.FromResult(true);
        }
    }
}
