using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Loads the curated Copilot assessment catalog and runs the active assessment suite.
    /// The same catalog definitions drive the assessment lab and Copilot sample libraries.
    /// </summary>
    public class CopilotAssessmentService
    {
        private const string DefaultSurface = "default";

        private readonly ICopilotChatEngine _copilotService;
        private readonly CopilotToolRegistryService _toolRegistry;
        private readonly ILogger<CopilotAssessmentService> _logger;
        private readonly string _catalogPath;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

        public CopilotAssessmentService(
            ICopilotChatEngine copilotService,
            CopilotToolRegistryService toolRegistry,
            IWebHostEnvironment environment,
            ILogger<CopilotAssessmentService> logger)
        {
            _copilotService = copilotService;
            _toolRegistry = toolRegistry;
            _logger = logger;
            _catalogPath = Path.Combine(environment.ContentRootPath, "Services", "AI", "Copilot", "Assessment", "CopilotAssessmentCatalog.json");
        }

        public async Task<CopilotAssessmentLabViewModel> GetAssessmentLabViewModelAsync()
        {
            var cases = await LoadCatalogCasesAsync();
            var assessmentCases = cases
                .Where(testCase => testCase.IncludeInAssessmentSuite)
                .ToList();

            return new CopilotAssessmentLabViewModel
            {
                CaseGroups = assessmentCases
                    .GroupBy(testCase => new { testCase.Category, testCase.CategoryDescription })
                    .OrderBy(group => group.Min(testCase => testCase.SortOrder))
                    .Select(group => new CopilotAssessmentCaseGroup
                    {
                        Category = group.Key.Category,
                        Description = group.Key.CategoryDescription,
                        Cases = group.OrderBy(testCase => testCase.SortOrder).ToList()
                    })
                    .ToList(),
                CopilotSampleGroups = BuildCopilotSampleGroups(cases, DefaultSurface)
            };
        }

        public async Task<List<CopilotPromptGroup>> GetCopilotPromptGroupsAsync(string surface = DefaultSurface)
        {
            var cases = await LoadCatalogCasesAsync();
            return BuildCopilotSampleGroups(cases, surface);
        }

        /// <summary>
        /// Runs a batch of assessment cases against the live copilot stack.
        /// Every case is recorded, including failures and exceptions.
        /// </summary>
        public async Task<CopilotAssessmentReport> RunAssessmentAsync(IEnumerable<CopilotAssessmentCase> cases)
        {
            var report = new CopilotAssessmentReport();

            foreach (var testCase in cases.OrderBy(item => item.SortOrder).ThenBy(item => item.Question))
            {
                try
                {
                    var request = new CopilotChatRequest
                    {
                        Question = testCase.Question,
                        Surface = "assessment",
                        History = CloneHistory(testCase.SeedHistory)
                    };

                    var response = await _copilotService.AskAsync(request);
                    report.Results.Add(new CopilotAssessmentResult
                    {
                        Case = testCase,
                        ActualResponse = response
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to run assessment case: {Question}", testCase.Question);
                    report.Results.Add(new CopilotAssessmentResult
                    {
                        Case = testCase,
                        FailureReason = ex.GetBaseException().Message
                    });
                }
            }

            return report;
        }

        public CopilotAssessmentRunSummaryDto BuildRunSummary(CopilotAssessmentReport report, int summaryId)
        {
            return new CopilotAssessmentRunSummaryDto
            {
                SummaryId = summaryId,
                RunAt = report.RunAt,
                TotalCases = report.TotalCases,
                SuccessCount = report.SuccessCount,
                SuccessRate = report.SuccessRate,
                AverageLatencyMs = report.AverageLatencyMs,
                Results = report.Results
                    .OrderBy(result => result.Case.SortOrder)
                    .ThenBy(result => result.Case.Question)
                    .Select(result => new CopilotAssessmentCaseResultDto
                    {
                        Id = result.Case.Id,
                        Category = result.Case.Category,
                        Question = result.Case.Question,
                        ExpectedBehavior = result.Case.ExpectedBehaviorSummary,
                        ActualMode = result.ActualMode,
                        ActualIntent = result.ActualIntent,
                        ActualTool = result.ActualTool,
                        Detail = result.Detail,
                        AnswerPreview = result.AnswerPreview,
                        LatencyMs = result.LatencyMs,
                        IsSuccess = result.IsSuccess
                    })
                    .ToList()
            };
        }

        /// <summary>
        /// Provides the active curated suite of assessment scenarios.
        /// </summary>
        public async Task<List<CopilotAssessmentCase>> GetDefaultTestSuiteAsync()
        {
            var suite = (await LoadCatalogCasesAsync())
                .Where(testCase => testCase.IncludeInAssessmentSuite)
                .OrderBy(testCase => testCase.SortOrder)
                .ThenBy(testCase => testCase.Question)
                .ToList();

            var enabledTools = await _toolRegistry.GetEnabledToolsAsync();
            var nextSortOrder = suite.Count == 0 ? 10 : suite.Max(testCase => testCase.SortOrder) + 10;

            foreach (var tool in enabledTools.Where(item => !string.IsNullOrWhiteSpace(item.TestPrompt)))
            {
                var expectedMode = Enum.TryParse<CopilotChatMode>(tool.CopilotMode, ignoreCase: true, out var parsedMode)
                    ? parsedMode
                    : CopilotChatMode.ExternalUtility;

                foreach (var prompt in SplitToolPrompts(tool.TestPrompt!))
                {
                    suite.Add(new CopilotAssessmentCase
                    {
                        Question = prompt,
                        Category = "Tool",
                        CategoryDescription = "Enabled external-tool smoke tests generated from the active tool registry.",
                        LibraryGroup = "External Tools",
                        IncludeInCopilotLibrary = false,
                        IncludeInAssessmentSuite = true,
                        LibrarySurfaces = ["all"],
                        SortOrder = nextSortOrder,
                        ExpectedIntent = CopilotIntentKind.ExternalToolQuery,
                        ExpectedMode = expectedMode,
                        ExpectedToolKey = tool.ToolKey
                    });

                    nextSortOrder += 10;
                }
            }

            return suite
                .OrderBy(testCase => testCase.SortOrder)
                .ThenBy(testCase => testCase.Question)
                .ToList();
        }

        private async Task<List<CopilotAssessmentCase>> LoadCatalogCasesAsync()
        {
            if (!File.Exists(_catalogPath))
            {
                throw new FileNotFoundException("Copilot assessment catalog file was not found.", _catalogPath);
            }

            await using var stream = File.OpenRead(_catalogPath);
            var cases = await JsonSerializer.DeserializeAsync<List<CopilotAssessmentCase>>(stream, _jsonOptions) ?? new List<CopilotAssessmentCase>();

            foreach (var testCase in cases)
            {
                NormalizeCase(testCase);
            }

            return cases
                .OrderBy(testCase => testCase.SortOrder)
                .ThenBy(testCase => testCase.Question)
                .ToList();
        }

        private static void NormalizeCase(CopilotAssessmentCase testCase)
        {
            testCase.Category = string.IsNullOrWhiteSpace(testCase.Category) ? "General" : testCase.Category.Trim();
            testCase.LibraryGroup = string.IsNullOrWhiteSpace(testCase.LibraryGroup) ? "General" : testCase.LibraryGroup.Trim();
            testCase.CategoryDescription ??= string.Empty;
            testCase.SeedHistory ??= new List<CopilotChatMessage>();
            testCase.LibrarySurfaces = testCase.LibrarySurfaces?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (testCase.LibrarySurfaces.Count == 0)
            {
                testCase.LibrarySurfaces.Add(DefaultSurface);
            }
        }

        private static List<CopilotPromptGroup> BuildCopilotSampleGroups(IEnumerable<CopilotAssessmentCase> cases, string surface)
        {
            return cases
                .Where(testCase => testCase.IncludeInCopilotLibrary && testCase.SupportsSurface(surface))
                .GroupBy(testCase => testCase.LibraryGroup)
                .OrderBy(group => group.Min(testCase => testCase.SortOrder))
                .Select(group => new CopilotPromptGroup
                {
                    Title = group.Key,
                    Prompts = group
                        .OrderBy(testCase => testCase.SortOrder)
                        .Select(testCase => testCase.Question)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(6)
                        .ToList()
                })
                .Where(group => group.Prompts.Count > 0)
                .ToList();
        }

        private static List<CopilotChatMessage> CloneHistory(IEnumerable<CopilotChatMessage> history)
        {
            return history
                .Select(message => new CopilotChatMessage
                {
                    Role = message.Role,
                    Content = message.Content
                })
                .ToList();
        }

        private static IEnumerable<string> SplitToolPrompts(string promptText)
        {
            return promptText
                .Split(new[] { "\r\n", "\n", "||" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
