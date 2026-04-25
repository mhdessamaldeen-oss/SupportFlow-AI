using System.Text.Json;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models.AI;
using Microsoft.EntityFrameworkCore;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class BilingualRetrievalBenchmarkService
    {
        private readonly IWebHostEnvironment _env;
        private readonly ApplicationDbContext _context;
        private readonly ISemanticSearchService _semanticSearchService;
        private readonly ILogger<BilingualRetrievalBenchmarkService> _logger;
        private const string DefaultRelativePath = "Benchmarks/bilingual_retrieval_benchmark.json";

        public BilingualRetrievalBenchmarkService(
            IWebHostEnvironment env,
            ApplicationDbContext context,
            ISemanticSearchService semanticSearchService,
            ILogger<BilingualRetrievalBenchmarkService> logger)
        {
            _env = env;
            _context = context;
            _semanticSearchService = semanticSearchService;
            _logger = logger;
        }

        public string GetDefaultPath()
        {
            return Path.Combine(_env.ContentRootPath, DefaultRelativePath);
        }

        public async Task<BilingualRetrievalBenchmark> LoadAsync(string? path = null, CancellationToken cancellationToken = default)
        {
            var resolvedPath = path ?? GetDefaultPath();

            if (!File.Exists(resolvedPath))
            {
                _logger.LogWarning("Bilingual retrieval benchmark file was not found at {Path}", resolvedPath);
                return new BilingualRetrievalBenchmark();
            }

            await using var stream = File.OpenRead(resolvedPath);
            var benchmark = await JsonSerializer.DeserializeAsync<BilingualRetrievalBenchmark>(
                stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                },
                cancellationToken);

            if (benchmark == null)
            {
                _logger.LogWarning("Failed to deserialize bilingual retrieval benchmark from {Path}", resolvedPath);
                return new BilingualRetrievalBenchmark();
            }

            benchmark.Cases = benchmark.Cases
                .Where(c => c.IsValid)
                .ToList();

            return benchmark;
        }

        public async Task<BilingualRetrievalBenchmarkRunResult> RunAsync(string? path = null, string? bucket = null, string? caseId = null, CancellationToken cancellationToken = default)
        {
            var benchmark = await LoadAsync(path, cancellationToken);
            var selectedCases = benchmark.Cases
                .Where(c => (string.IsNullOrWhiteSpace(bucket) || string.Equals(c.Bucket, bucket, StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrWhiteSpace(caseId) || string.Equals(c.Id, caseId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var existingIds = await LoadExistingTicketIdsAsync(selectedCases, cancellationToken);

            var results = new List<RetrievalBenchmarkCaseResult>();

            foreach (var testCase in selectedCases)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var matches = testCase.SourceTicketId.HasValue
                    ? await _semanticSearchService.GetRelatedTicketsAsync(testCase.SourceTicketId.Value, testCase.Count, testCase.StatusIds, testCase.IncludeAllStatuses)
                    : await _semanticSearchService.SearchSimilarTicketsByTextAsync(testCase.QueryText, testCase.Count, testCase.StatusIds, testCase.IncludeAllStatuses);

                var returnedTicketIds = matches.Select(m => m.Ticket.Id).ToList();
                var hasExpectation = testCase.ExpectedTicketIds.Count > 0;
                var missingExpectedTicketIds = testCase.ExpectedTicketIds
                    .Where(id => !existingIds.Contains(id))
                    .ToList();
                var isHit = hasExpectation
                    ? testCase.ExpectedTicketIds.Intersect(returnedTicketIds).Any()
                    : (bool?)null;

                results.Add(new RetrievalBenchmarkCaseResult
                {
                    Id = testCase.Id,
                    Bucket = testCase.Bucket,
                    QueryLanguage = testCase.QueryLanguage,
                    QueryText = testCase.QueryText,
                    Intent = testCase.Intent,
                    SourceTicketId = testCase.SourceTicketId,
                    IsSourceTicketMissing = testCase.SourceTicketId.HasValue && !existingIds.Contains(testCase.SourceTicketId.Value),
                    HasExpectation = hasExpectation,
                    IsHit = isHit,
                    ExpectedTicketIds = testCase.ExpectedTicketIds,
                    MissingExpectedTicketIds = missingExpectedTicketIds,
                    ReturnedTicketIds = returnedTicketIds,
                    Matches = matches.Select(m => new RetrievalBenchmarkMatchResult
                    {
                        TicketId = m.Ticket.Id,
                        TicketNumber = m.Ticket.TicketNumber,
                        Title = m.Ticket.Title,
                        Score = Math.Round(m.Score * 100, 2)
                    }).ToList()
                });
            }

            var bucketResults = results
                .GroupBy(r => r.Bucket)
                .Select(g => new RetrievalBenchmarkBucketResult
                {
                    Bucket = g.Key,
                    TotalCases = g.Count(),
                    EvaluatedCases = g.Count(x => x.HasExpectation),
                    HitCases = g.Count(x => x.IsHit == true)
                })
                .OrderBy(r => r.Bucket)
                .ToList();

            var runResult = new BilingualRetrievalBenchmarkRunResult
            {
                Version = benchmark.Version,
                RunOnUtc = DateTime.UtcNow,
                TotalCases = results.Count,
                EvaluatedCases = results.Count(r => r.HasExpectation),
                HitCases = results.Count(r => r.IsHit == true),
                Buckets = bucketResults,
                Cases = results
            };

            // Capture history for full or bucket runs (skip for single specific cases to avoid noise)
            if (string.IsNullOrEmpty(caseId))
            {
                try
                {
                    var settings = await _semanticSearchService.GetTuningSettingsAsync();
                    var run = new RetrievalBenchmarkRun
                {
                    RunOnUtc = runResult.RunOnUtc,
                    TotalCases = runResult.TotalCases,
                    EvaluatedCases = runResult.EvaluatedCases,
                    HitCases = runResult.HitCases,
                    HitRate = runResult.EvaluatedCases > 0 ? (double)runResult.HitCases / runResult.EvaluatedCases : 0,
                    Version = runResult.Version,
                    SettingsJson = JsonSerializer.Serialize(settings),
                    ResultsJson = JsonSerializer.Serialize(results.Select(r => new { 
                        r.Id, 
                        r.IsHit, 
                        Score = r.Matches.Any() ? r.Matches[0].Score : 0 
                    }))
                };
                _context.RetrievalBenchmarkRuns.Add(run);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save benchmark run history");
            }
            } // Close if (string.IsNullOrEmpty(caseId))

            return runResult;
        }

        public async Task<RetrievalBenchmarkValidationResult> ValidateAsync(string? path = null, string? bucket = null, CancellationToken cancellationToken = default)
        {
            var benchmark = await LoadAsync(path, cancellationToken);
            var selectedCases = benchmark.Cases
                .Where(c => string.IsNullOrWhiteSpace(bucket) || string.Equals(c.Bucket, bucket, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var existingIds = await LoadExistingTicketIdsAsync(selectedCases, cancellationToken);

            var cases = selectedCases.Select(testCase =>
            {
                var missingExpected = testCase.ExpectedTicketIds
                    .Where(id => !existingIds.Contains(id))
                    .ToList();
                var sourceMissing = testCase.SourceTicketId.HasValue && !existingIds.Contains(testCase.SourceTicketId.Value);
                var warnings = new List<string>();

                if (sourceMissing && testCase.SourceTicketId.HasValue)
                {
                    warnings.Add($"Missing source ticket id: {testCase.SourceTicketId.Value}");
                }

                if (missingExpected.Count > 0)
                {
                    warnings.Add($"Missing expected ticket ids: {string.Join(", ", missingExpected)}");
                }

                return new RetrievalBenchmarkValidationCase
                {
                    Id = testCase.Id,
                    Bucket = testCase.Bucket,
                    SourceTicketId = testCase.SourceTicketId,
                    IsSourceTicketMissing = sourceMissing,
                    ExpectedTicketIds = testCase.ExpectedTicketIds,
                    MissingExpectedTicketIds = missingExpected,
                    Warnings = warnings
                };
            }).ToList();

            return new RetrievalBenchmarkValidationResult
            {
                Version = benchmark.Version,
                ValidatedOnUtc = DateTime.UtcNow,
                TotalCases = cases.Count,
                CasesWithWarnings = cases.Count(c => c.Warnings.Count > 0),
                Cases = cases
            };
        }

        private async Task<HashSet<int>> LoadExistingTicketIdsAsync(IEnumerable<RetrievalBenchmarkCase> cases, CancellationToken cancellationToken)
        {
            var relevantIds = cases
                .SelectMany(c => c.ExpectedTicketIds.Concat(c.SourceTicketId.HasValue ? new[] { c.SourceTicketId.Value } : Array.Empty<int>()))
                .Distinct()
                .ToList();

            if (relevantIds.Count == 0)
            {
                return new HashSet<int>();
            }

            var ids = await _context.Tickets
                .AsNoTracking()
                .Where(t => relevantIds.Contains(t.Id) && !t.IsDeleted)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            return ids.ToHashSet();
        }
    }
}
