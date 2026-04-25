using AISupportAnalysisPlatform.Enums;
using AISupportAnalysisPlatform.Constants;
using System.Text.Json;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Models.Common;
using AISupportAnalysisPlatform.Models.DTOs;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using AISupportAnalysisPlatform.Services.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AISupportAnalysisPlatform.Services.Infrastructure;

namespace AISupportAnalysisPlatform.Controllers.AI
{
    [Authorize(Roles = RoleNames.Admin)]
    [Route("AiAnalysis/[action]")]
    public class AiAnalysisController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly AiAnalysisQueueService _queueService;
        private readonly EmbeddingQueueService _embeddingService;
        private readonly ISemanticSearchService _semanticSearchService;
        private readonly BilingualRetrievalBenchmarkService _benchmarkService;
        private readonly CopilotRecommendationEngine _recommendationEngine;
        private readonly KnowledgeBaseRagService _knowledgeBaseRagService;
        private readonly CopilotEvaluationEngine _evaluationEngine;
        private readonly ICopilotChatEngine _chatEngine;
        private readonly CopilotToolRegistryService _toolRegistry;
        private readonly CopilotAssessmentService _assessmentService;
        private readonly ILocalizationService _localizer;
        private readonly IMapper _mapper;

        public AiAnalysisController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IServiceScopeFactory scopeFactory,
            AiAnalysisQueueService queueService,
            EmbeddingQueueService embeddingService,
            ISemanticSearchService semanticSearchService,
            BilingualRetrievalBenchmarkService benchmarkService,
            CopilotRecommendationEngine recommendationEngine,
            KnowledgeBaseRagService knowledgeBaseRagService,
            CopilotEvaluationEngine evaluationEngine,
            ICopilotChatEngine chatEngine,
            CopilotToolRegistryService toolRegistry,
            CopilotAssessmentService assessmentService,
            ILocalizationService localizer,
            IMapper mapper)
        {
            _context = context;
            _userManager = userManager;
            _scopeFactory = scopeFactory;
            _queueService = queueService;
            _embeddingService = embeddingService;
            _semanticSearchService = semanticSearchService;
            _benchmarkService = benchmarkService;
            _recommendationEngine = recommendationEngine;
            _knowledgeBaseRagService = knowledgeBaseRagService;
            _evaluationEngine = evaluationEngine;
            _chatEngine = chatEngine;
            _toolRegistry = toolRegistry;
            _assessmentService = assessmentService;
            _localizer = localizer;
            _mapper = mapper;
        }

        /// <summary>
        /// Returns the latest (highest RunNumber) analysis for a ticket.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetStatus(int id)
        {
            var analysis = await _context.TicketAiAnalyses
                .Where(a => a.TicketId == id)
                .OrderByDescending(a => a.RunNumber)
                .FirstOrDefaultAsync();

            if (analysis == null)
            {
                // Check if it's queued but not yet started
                var qStatus = _queueService.GetTicketStatus(id);
                if (qStatus != null && (qStatus.Status == AiAnalysisStatus.Queued || qStatus.Status == AiAnalysisStatus.InProgress))
                {
                    return Json(ApiResponse<AiAnalysisStatusDto>.Ok(new AiAnalysisStatusDto 
                    { 
                        Status = qStatus.Status.ToString(), 
                        QueueStatus = qStatus.Status.ToString() 
                    }));
                }
                return Json(ApiResponse<AiAnalysisStatusDto>.Ok(new AiAnalysisStatusDto 
                { 
                    Status = AiAnalysisStatus.NotStarted.ToString() 
                }));
            }

            var attachments = new List<object>();

            string latestLog = "";
            if (analysis.AnalysisStatus == AiAnalysisStatus.InProgress)
            {
                var log = await _context.TicketAiAnalysisLogs
                    .Where(l => l.TicketAiAnalysisId == analysis.Id)
                    .OrderByDescending(l => l.CreatedOn)
                    .FirstOrDefaultAsync();
                if (log != null) latestLog = log.Message;
            }

            var dto = _mapper.Map<TicketAiAnalysisDto>(analysis);
            dto.LatestLog = latestLog;
            return Json(ApiResponse<TicketAiAnalysisDto>.Ok(dto));
        }

        /// <summary>
        /// Returns a specific run's analysis for a ticket.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRun(int ticketId, int runNumber)
        {
            var analysis = await _context.TicketAiAnalyses
                .Where(a => a.TicketId == ticketId && a.RunNumber == runNumber)
                .FirstOrDefaultAsync();

            if (analysis == null)
                return Json(ApiResponse<AiAnalysisStatusDto>.Ok(new AiAnalysisStatusDto { Status = "NotStarted" }));

            var attachments = new List<object>();

            var dto = _mapper.Map<TicketAiAnalysisDto>(analysis);
            return Json(ApiResponse<TicketAiAnalysisDto>.Ok(dto));
        }

        /// <summary>
        /// Returns run history (lightweight) for a ticket.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRunHistory(int ticketId)
        {
            var runs = await _context.TicketAiAnalyses
                .Where(a => a.TicketId == ticketId)
                .OrderByDescending(a => a.RunNumber)
                .ProjectTo<TicketRunHistoryDto>(_mapper.ConfigurationProvider)
                .ToListAsync();

            return Json(ApiResponse<List<TicketRunHistoryDto>>.Ok(runs));
        }

        [HttpGet]
        public async Task<IActionResult> GetLogs(int ticketId)
        {
            var analysis = await _context.TicketAiAnalyses
                .Where(a => a.TicketId == ticketId)
                .OrderByDescending(a => a.RunNumber)
                .Select(a => new { a.Id })
                .FirstOrDefaultAsync();

            if (analysis == null) return Json(new List<object>());

            var logs = await _context.TicketAiAnalysisLogs
                .Where(l => l.TicketAiAnalysisId == analysis.Id)
                .OrderByDescending(l => l.CreatedOn)
                .Take(200)
                .OrderBy(l => l.CreatedOn)
                .ProjectTo<AiLogDto>(_mapper.ConfigurationProvider)
                .ToListAsync();

            return Json(ApiResponse<List<AiLogDto>>.Ok(logs));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetSimilarSolutions(int id)
        {
            try
            {
                var matches = await _semanticSearchService.GetRelatedTicketsAsync(id, count: 3);
                
                var results = _mapper.Map<List<AiSearchMatchDto>>(matches);

                return Json(ApiResponse<List<AiSearchMatchDto>>.Ok(results));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail("Error retrieving similar solutions: " + ex.Message));
            }
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetRecommendation(int id)
        {
            try
            {
                var recommendation = await _recommendationEngine.GenerateAsync(id);
                var dto = _mapper.Map<AiRecommendationDto>(recommendation);
                // SimilarTickets and KnowledgeMatches still need manual mapping or nested mapping profile
                dto.SimilarTickets = _mapper.Map<List<AiSearchMatchDto>>(recommendation.SimilarTickets);
                dto.KnowledgeMatches = _mapper.Map<List<KnowledgeMatchDto>>(recommendation.KnowledgeMatches);

                return Json(ApiResponse<AiRecommendationDto>.Ok(dto));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail("Error generating recommendation: " + ex.Message));
            }
        }

        [HttpGet]
        public IActionResult Benchmark()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> CopilotAssessment()
        {
            var model = await _assessmentService.GetAssessmentLabViewModelAsync();
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> RunCopilotAssessment()
        {
            var suite = await _assessmentService.GetDefaultTestSuiteAsync();
            var report = await _assessmentService.RunAssessmentAsync(suite);

            var run = new CopilotAssessmentRun
            {
                RunOnUtc = report.RunAt,
                TotalCases = report.TotalCases,
                SuccessCount = report.SuccessCount,
                SuccessRate = report.SuccessRate,
                AverageLatencyMs = report.AverageLatencyMs,
                ResultsJson = string.Empty,
                Version = "2.0"
            };

            _context.CopilotAssessmentRuns.Add(run);
            await _context.SaveChangesAsync();

            var summary = _assessmentService.BuildRunSummary(report, run.Id);
            run.ResultsJson = JsonSerializer.Serialize(summary.Results);
            await _context.SaveChangesAsync();

            return Json(ApiResponse<CopilotAssessmentRunSummaryDto>.Ok(
                summary,
                string.Format(_localizer.Get("BenchmarkStarted"), report.TotalCases)));
        }

        [HttpGet]
        public async Task<IActionResult> Copilot(string? prompt = null)
        {
            var enabledTools = (await _toolRegistry.GetAllToolsAsync())
                .Where(t => t.IsEnabled)
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.Title)
                .ToList();

            var recentTickets = await _context.Tickets
                .Where(t => !t.IsDeleted)
                .OrderByDescending(t => t.CreatedAt)
                .Take(20)
                .Select(t => new CopilotEvaluationTicketItem
                {
                    TicketId = t.Id,
                    TicketNumber = t.TicketNumber,
                    Title = t.Title,
                    Status = t.Status != null ? t.Status.Name : "",
                    ProductArea = t.ProductArea ?? "",
                    CreatedAt = t.CreatedAt
                })
                .ToListAsync();

            var recentTraces = await _context.CopilotTraceHistories
                .OrderByDescending(t => t.CreatedAt)
                .Take(10)
                .ToListAsync();

            var model = new CopilotChatViewModel
            {
                RecentTickets = recentTickets,
                AvailableTools = enabledTools,
                ExternalCapabilities = BuildExternalCapabilities(enabledTools),
                StandardPromptGroups = await _assessmentService.GetCopilotPromptGroupsAsync(),
                RecentTraces = recentTraces,
                KnowledgeDocumentCount = _knowledgeBaseRagService.GetDocumentCount()
            };

            ViewData["CopilotInitialPrompt"] = prompt ?? string.Empty;
            return View(model);
        }

        private static List<CopilotCapabilityItem> BuildExternalCapabilities(IEnumerable<CopilotToolDefinition> tools)
        {
            return tools
                .Where(tool => string.Equals(tool.ToolType, "External", StringComparison.OrdinalIgnoreCase))
                .Select(tool => new CopilotCapabilityItem
                {
                    ToolKey = tool.ToolKey,
                    ToolTitle = tool.Title,
                    ToolDescription = tool.Description,
                    Prompts = SplitCapabilityPrompts(tool.TestPrompt, tool.Description)
                })
                .Where(item => item.Prompts.Any())
                .ToList();
        }

        private static List<string> SplitCapabilityPrompts(string? testPrompt, string fallbackDescription)
        {
            var promptText = string.IsNullOrWhiteSpace(testPrompt) ? fallbackDescription : testPrompt;
            return promptText
                .Split(new[] { "\r\n", "\n", "||" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [HttpGet]
        public async Task<IActionResult> InvestigationHistory(int? traceId = null, string? view = null)
        {
            var traces = await _context.CopilotTraceHistories
                .OrderByDescending(t => t.CreatedAt)
                .Take(100)
                .ToListAsync();
            ViewBag.InitialTraceId = traceId;
            ViewBag.InitialTraceView = string.Equals(view, "tree", StringComparison.OrdinalIgnoreCase) ? "tree" : "story";
            return View(traces);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteInvestigationHistory(List<int> selectedTraceIds)
        {
            if (selectedTraceIds == null || selectedTraceIds.Count == 0)
            {
                TempData["Error"] = "Select at least one investigation trace to delete.";
                return RedirectToAction(nameof(InvestigationHistory));
            }

            var ids = selectedTraceIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            var traces = await _context.CopilotTraceHistories
                .Where(trace => ids.Contains(trace.Id))
                .ToListAsync();

            if (traces.Count == 0)
            {
                TempData["Error"] = "No matching investigation traces were found.";
                return RedirectToAction(nameof(InvestigationHistory));
            }

            _context.CopilotTraceHistories.RemoveRange(traces);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"{traces.Count} investigation trace{(traces.Count == 1 ? "" : "s")} deleted.";
            return RedirectToAction(nameof(InvestigationHistory));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> InvestigationStory(int id, string? view = null)
        {
            var trace = await _context.CopilotTraceHistories.FindAsync(id);
            if (trace == null) return NotFound();

            var executionDetails = string.IsNullOrWhiteSpace(trace.ExecutionDetailsJson)
                ? new AdminCopilotExecutionDetails()
                : JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(trace.ExecutionDetailsJson) ?? new AdminCopilotExecutionDetails();

            var model = new CopilotTraceDetailPageViewModel
            {
                Trace = trace,
                ExecutionDetails = executionDetails,
                ArchiveCount = await _context.CopilotTraceHistories.CountAsync(),
                DefaultTraceView = string.Equals(view, "tree", StringComparison.OrdinalIgnoreCase) ? "tree" : "story"
            };

            return View(model);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> TracingDetails(int id, string? view = null)
        {
            var trace = await _context.CopilotTraceHistories.FindAsync(id);
            if (trace == null) return NotFound();

            var executionDetails = string.IsNullOrWhiteSpace(trace.ExecutionDetailsJson) 
                ? new AdminCopilotExecutionDetails() 
                : JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(trace.ExecutionDetailsJson);

            ViewBag.Trace = trace;
            ViewBag.DefaultTraceView = string.Equals(view, "tree", StringComparison.OrdinalIgnoreCase) ? "tree" : "story";
            return PartialView("_InvestigationTraceDetail", executionDetails);
        }

        [HttpPost]
        public async Task<IActionResult> AskCopilot([FromBody] CopilotChatRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Question))
            {
                return BadRequest(new { message = "A question is required." });
            }

            var result = await _chatEngine.AskAsync(request, HttpContext.RequestAborted);
            return Json(result);
        }

        [HttpGet]
        public async Task<IActionResult> Evaluation()
        {
            var evaluations = await _evaluationEngine.LoadAsync();
            var ticketIds = evaluations.Select(e => e.TicketId).ToHashSet();

            var recentTickets = await _context.Tickets
                .Where(t => !t.IsDeleted)
                .OrderByDescending(t => t.CreatedAt)
                .Take(20)
                .Select(t => new CopilotEvaluationTicketItem
                {
                    TicketId = t.Id,
                    TicketNumber = t.TicketNumber,
                    Title = t.Title,
                    Status = t.Status != null ? t.Status.Name : "",
                    ProductArea = t.ProductArea ?? "",
                    CreatedAt = t.CreatedAt
                })
                .ToListAsync();

            var model = new CopilotEvaluationViewModel
            {
                Tickets = recentTickets,
                ExistingEvaluations = evaluations.OrderByDescending(e => e.EvaluatedOnUtc).ToList(),
                TotalEvaluations = evaluations.Count,
                PassedEvaluations = evaluations.Count(e => string.Equals(e.OverallOutcome, "Pass", StringComparison.OrdinalIgnoreCase)),
                FailedEvaluations = evaluations.Count(e => string.Equals(e.OverallOutcome, "Fail", StringComparison.OrdinalIgnoreCase))
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveEvaluation([FromForm] CopilotEvaluationEntry entry)
        {
            if (entry.TicketId <= 0)
            {
                TempData["Error"] = "Ticket is required for Copilot evaluation.";
                return RedirectToAction(nameof(Evaluation));
            }

            var user = await _userManager.GetUserAsync(User);
            entry.EvaluatedBy = user?.FullName ?? user?.UserName ?? "Admin";
            entry.EvaluatedOnUtc = DateTime.UtcNow;

            await _evaluationEngine.SaveAsync(entry);
            TempData["Success"] = $"Evaluation saved for {entry.TicketNumber}.";
            return RedirectToAction(nameof(Evaluation));
        }

        [HttpGet]
        public IActionResult KnowledgeBase()
        {
            var model = new KnowledgeBaseAdminViewModel
            {
                RootPath = _knowledgeBaseRagService.KnowledgeBaseRootPath,
                Categories = _knowledgeBaseRagService.GetManagedFolders().ToList(),
                Documents = _knowledgeBaseRagService.GetManagedDocuments().ToList()
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadKnowledgeDocument(IFormFile? file, string category)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "No file was selected.";
                return RedirectToAction(nameof(KnowledgeBase));
            }

            if (!_knowledgeBaseRagService.IsManagedCategory(category))
            {
                TempData["Error"] = "Invalid knowledge-base category.";
                return RedirectToAction(nameof(KnowledgeBase));
            }

            var extension = Path.GetExtension(file.FileName);
            if (!string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only .md and .txt files are supported.";
                return RedirectToAction(nameof(KnowledgeBase));
            }

            var safeFileName = Path.GetFileName(file.FileName);
            var targetDirectory = _knowledgeBaseRagService.GetCategoryDirectory(category);
            Directory.CreateDirectory(targetDirectory);

            var targetPath = Path.Combine(targetDirectory, safeFileName);
            await using (var stream = System.IO.File.Create(targetPath))
            {
                await file.CopyToAsync(stream);
            }

            _knowledgeBaseRagService.InvalidateDocument(targetPath);
            TempData["Success"] = $"{safeFileName} uploaded to {category}.";
            return RedirectToAction(nameof(KnowledgeBase));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteKnowledgeDocument(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                TempData["Error"] = "Knowledge document path is missing.";
                return RedirectToAction(nameof(KnowledgeBase));
            }

            var fullPath = Path.GetFullPath(Path.Combine(_knowledgeBaseRagService.KnowledgeBaseRootPath, relativePath));
            var rootPath = Path.GetFullPath(_knowledgeBaseRagService.KnowledgeBaseRootPath);
            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Invalid knowledge document path.";
                return RedirectToAction(nameof(KnowledgeBase));
            }

            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
                _knowledgeBaseRagService.InvalidateDocument(fullPath);
                TempData["Success"] = $"{Path.GetFileName(fullPath)} deleted.";
            }
            else
            {
                TempData["Error"] = "Knowledge document was not found.";
            }

            return RedirectToAction(nameof(KnowledgeBase));
        }

        [HttpGet]
        public async Task<IActionResult> GetTuningSettings()
        {
            var settings = await _semanticSearchService.GetTuningSettingsAsync();
            return Ok(settings);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateTuningSettings([FromBody] AISupportAnalysisPlatform.Models.AI.RetrievalTuningSettings settings)
        {
            await _semanticSearchService.UpdateTuningSettingsAsync(settings);
            return Ok(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> GetRetrievalBenchmark()
        {
            try
            {
                var benchmark = await _benchmarkService.LoadAsync();
                return Json(ApiResponse<BenchmarkDto>.Ok(new BenchmarkDto
                {
                    Version = benchmark.Version,
                    CreatedOnUtc = benchmark.CreatedOnUtc,
                    CaseCount = benchmark.Cases.Count,
                    Cases = benchmark.Cases.Select(c => new BenchmarkCaseDto
                    {
                        Id = c.Id,
                        Bucket = c.Bucket,
                        QueryLanguage = c.QueryLanguage,
                        QueryText = c.QueryText,
                        SourceTicketId = c.SourceTicketId,
                        Count = c.Count,
                        IncludeAllStatuses = c.IncludeAllStatuses,
                        StatusIds = c.StatusIds,
                        ExpectedTicketIds = c.ExpectedTicketIds,
                        Intent = c.Intent,
                        Notes = c.Notes
                    }).ToList()
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse.Fail("Backend error loading benchmark file: " + ex.Message));
            }
        }

        [HttpGet]
        public async Task<IActionResult> RunRetrievalBenchmark(string? bucket = null, string? caseId = null)
        {
            var result = await _benchmarkService.RunAsync(bucket: bucket, caseId: caseId);
            return Json(ApiResponse<RetrievalBenchmarkResultDto>.Ok(_mapper.Map<RetrievalBenchmarkResultDto>(result)));
        }

        [HttpGet]
        public async Task<IActionResult> GetBenchmarkHistory()
        {
            var history = await _context.RetrievalBenchmarkRuns
                .OrderByDescending(r => r.RunOnUtc)
                .Take(10)
                .Select(r => new BenchmarkHistoryDto
                {
                    Id = r.Id,
                    RunOnUtc = r.RunOnUtc,
                    TotalCases = r.TotalCases,
                    EvaluatedCases = r.EvaluatedCases,
                    HitCases = r.HitCases,
                    HitRate = r.HitRate,
                    Version = r.Version,
                    SettingsJson = r.SettingsJson,
                    ResultsJson = r.ResultsJson
                })
                .ToListAsync();
            return Json(ApiResponse<List<BenchmarkHistoryDto>>.Ok(history));
        }

        [HttpGet]
        public async Task<IActionResult> ValidateRetrievalBenchmark(string? bucket = null)
        {
            var result = await _benchmarkService.ValidateAsync(bucket: bucket);
            return Json(ApiResponse<BenchmarkValidationDto>.Ok(_mapper.Map<BenchmarkValidationDto>(result)));
        }

        [HttpPost("{id}")]
        public async Task<IActionResult> RunAnalysis(int id)
        {
            var user = await _userManager.GetUserAsync(User);
            var userId = user!.Id;

            // Use the queue for sequential processing
            _queueService.Enqueue(id, userId, isRefresh: false);

            return Json(ApiResponse.Ok(_localizer.Get("AnalysisQueued")));
        }

        [HttpPost("{id}")]
        public async Task<IActionResult> RefreshAnalysis(int id)
        {
            var user = await _userManager.GetUserAsync(User);
            var userId = user!.Id;

            // Use the queue for sequential processing
            _queueService.Enqueue(id, userId, isRefresh: true);

            return Json(ApiResponse.Ok(_localizer.Get("ReAnalysisQueued")));
        }

        /// <summary>
        /// Batch analysis — enqueues all tickets for SEQUENTIAL processing through the queue.
        /// No more parallel fire-and-forget.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> RunBulkAnalysis([FromBody] List<int>? ticketIds)
        {
            var user = await _userManager.GetUserAsync(User);
            var userId = user!.Id;

            var idsToAnalyze = ticketIds != null && ticketIds.Any()
                ? ticketIds
                : await _context.Tickets
                    .Where(t => !t.IsDeleted)
                    .Select(t => t.Id)
                    .ToListAsync();

            if (!idsToAnalyze.Any())
                return BadRequest(_localizer.Get("NoTicketsSelected"));

            var batchId = _queueService.EnqueueBatch(idsToAnalyze, userId);

            return Json(ApiResponse<AiBatchAnalysisDto>.Ok(new AiBatchAnalysisDto
            {
                Message = string.Format(_localizer.Get("TicketsQueuedForAnalysis"), idsToAnalyze.Count),
                BatchId = batchId,
                TotalCount = idsToAnalyze.Count
            }));
        }

        [HttpPost]
        public IActionResult StopBatchAnalysis()
        {
            _queueService.StopBatchProcess();
            return Json(ApiResponse.Ok(_localizer.Get("BatchProcessingStopped")));
        }

        [HttpPost]
        public async Task<IActionResult> RunBulkEmbedding([FromBody] List<int>? ticketIds)
        {
            var idsToEmbed = ticketIds != null && ticketIds.Any()
                ? ticketIds
                : await _context.Tickets.Where(t => !t.IsDeleted).Select(t => t.Id).ToListAsync();

            var started = _embeddingService.StartBatch(idsToEmbed);
            if (!started)
                return Json(ApiResponse.Fail(_localizer.Get("EmbeddingBatchRunning")));

            return Json(ApiResponse.Ok(string.Format(_localizer.Get("EmbeddingProcessStarted"), idsToEmbed.Count)));
        }

        [HttpGet]
        public IActionResult GetEmbeddingProgress()
        {
            var p = _embeddingService.GetProgress();
            return Json(ApiResponse<EmbeddingProgressDto>.Ok(new EmbeddingProgressDto
            {
                TotalCount = p.TotalCount,
                CompletedCount = p.CompletedCount,
                FailedCount = p.FailedCount,
                CurrentTicketId = p.CurrentTicketId,
                IsRunning = p.IsRunning,
                ProcessedCount = p.ProcessedCount,
                ProgressPercent = p.ProgressPercent,
                LastErrorMessage = p.LastErrorMessage
            }));
        }

        [HttpPost]
        public IActionResult StopEmbedding()
        {
            _embeddingService.Stop();
            return Json(ApiResponse.Ok(_localizer.Get("EmbeddingProcessStopped")));
        }

        /// <summary>
        /// Real-time batch progress endpoint for the UI to poll.
        /// </summary>
        [HttpGet]
        public IActionResult GetBatchProgress()
        {
            var progress = _queueService.GetBatchProgress();
            var dto = _mapper.Map<BatchProgressDto>(progress);
            dto.QueueLength = _queueService.QueueLength;
            return Json(ApiResponse<BatchProgressDto>.Ok(dto));
        }

        /// <summary>
        /// Get the queue status for a specific ticket.
        /// </summary>
        [HttpGet("{id}")]
        public IActionResult GetQueueStatus(int id)
        {
            var status = _queueService.GetTicketStatus(id);
            if (status == null)
            {
                return Json(ApiResponse<ProvisioningLogDto>.Ok(new ProvisioningLogDto 
                { 
                    Status = "NotStarted", 
                    StatusLabel = _localizer.Get("NotStarted") 
                }));
            }

            return Json(ApiResponse<TicketQueueStatusDto>.Ok(new TicketQueueStatusDto
            {
                TicketId = status.TicketId,
                Status = status.Status.ToString(),
                StatusLabel = _localizer.Get(status.Status.ToString()),
                EnqueuedAt = status.EnqueuedAt.ToString("HH:mm:ss"),
                StartedAt = status.StartedAt?.ToString("HH:mm:ss"),
                CompletedAt = status.CompletedAt?.ToString("HH:mm:ss")
            }));
        }

        [HttpGet]
        public async Task<IActionResult> Hub([FromQuery] GridRequestModel request)
        {
            request.Normalize();

            var query = _context.Tickets
                .AsNoTracking()
                .Include(t => t.Status)
                .Include(t => t.Entity)
                .Where(t => !t.IsDeleted);

            if (!string.IsNullOrEmpty(request.SearchString))
            {
                query = query.Where(t => t.TicketNumber.Contains(request.SearchString) || t.Title.Contains(request.SearchString));
            }

            if (request.StatusId.HasValue)
                query = query.Where(t => t.StatusId == request.StatusId.Value);
            
            if (request.PriorityId.HasValue)
                query = query.Where(t => t.PriorityId == request.PriorityId.Value);
            
            if (request.EntityId.HasValue)
                query = query.Where(t => t.EntityId == request.EntityId.Value);
            
            if (request.CategoryId.HasValue)
                query = query.Where(t => t.CategoryId == request.CategoryId.Value);

            // Populate Dropdowns
            ViewBag.StatusId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(await _context.TicketStatuses.OrderBy(s => s.Name).ToListAsync(), "Id", "Name", request.StatusId);
            ViewBag.PriorityId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(await _context.TicketPriorities.OrderBy(p => p.Name).ToListAsync(), "Id", "Name", request.PriorityId);
            ViewBag.CategoryId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(await _context.TicketCategories.OrderBy(c => c.Name).ToListAsync(), "Id", "Name", request.CategoryId);
            ViewBag.EntityFilterId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(await _context.Entities.OrderBy(e => e.Name).ToListAsync(), "Id", "Name", request.EntityId);

            switch (request.SortOrder)
            {
                case "id_desc": query = query.OrderByDescending(t => t.TicketNumber); break;
                case "Id": query = query.OrderBy(t => t.TicketNumber); break;
                case "date_desc": query = query.OrderByDescending(t => t.CreatedAt); break;
                case "Title": query = query.OrderBy(t => t.Title); break;
                case "title_desc": query = query.OrderByDescending(t => t.Title); break;
                case "Entity": query = query.OrderBy(t => t.Entity!.Name); break;
                case "entity_desc": query = query.OrderByDescending(t => t.Entity!.Name); break;
                case "Status": query = query.OrderBy(t => t.Status!.Name); break;
                case "status_desc": query = query.OrderByDescending(t => t.Status!.Name); break;
                default: query = query.OrderByDescending(t => t.CreatedAt); break;
            }

            // Read this screen under ReadUncommitted to reduce contention with bulk AI jobs,
            // but execute the whole transaction through EF's retry strategy.
            _context.Database.SetCommandTimeout(120);
            var executionStrategy = _context.Database.CreateExecutionStrategy();

            var totalItems = 0;
            var effectivePageSize = request.PageSize;
            var tickets = new List<Ticket>();
            var latestAnalyses = new Dictionary<int, TicketAiAnalysis>();

            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadUncommitted);

                totalItems = await query.CountAsync();
                effectivePageSize = request.GetEffectivePageSize(totalItems);

                tickets = await query
                    .Skip((request.PageNumber - 1) * effectivePageSize)
                    .Take(effectivePageSize)
                    .ToListAsync();

                var visibleTicketIds = tickets.Select(t => t.Id).ToList();
                latestAnalyses = await _context.TicketAiAnalyses
                    .AsNoTracking()
                    .Where(a => visibleTicketIds.Contains(a.TicketId))
                    .GroupBy(a => a.TicketId)
                    .Select(g => g
                        .OrderByDescending(a => a.RunNumber)
                        .Select(a => new TicketAiAnalysis
                        {
                            TicketId = a.TicketId,
                            Summary = a.Summary,
                            AnalysisStatus = a.AnalysisStatus,
                            RunNumber = a.RunNumber
                        })
                        .First())
                    .ToDictionaryAsync(a => a.TicketId, a => a);

                await transaction.CommitAsync();
            });

            ViewBag.AnalysesByTicketId = latestAnalyses;

            var pagedResult = new PagedResult<Ticket>
            {
                Items = tickets,
                TotalCount = totalItems,
                PageNumber = request.PageNumber,
                PageSize = effectivePageSize,
                Request = request
            };

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("_HubGrid", pagedResult);
            }

            return View(pagedResult);
        }

        // Helper: deserialize legacy JSON blob for backward compat
        private static IEnumerable<object> DeserializeAttachmentJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "[]") return Enumerable.Empty<object>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.EnumerateArray().Select(e => new
                {
                    FileName = e.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "",
                    Summary = e.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "",
                    Relevance = e.TryGetProperty("relevance", out var rl) ? rl.GetString() ?? "Low" : "Low"
                }).ToList();
            }
            catch { return Enumerable.Empty<object>(); }
        }
    }
}

