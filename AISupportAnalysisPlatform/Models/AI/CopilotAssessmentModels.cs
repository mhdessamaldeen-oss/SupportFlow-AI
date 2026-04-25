using System;
using System.Collections.Generic;
using System.Linq;

namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Represents one curated assessment scenario for the admin copilot.
    /// The same scenario catalog can drive the lab page and the sample library.
    /// </summary>
    public class CopilotAssessmentCase
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Question { get; set; } = string.Empty;
        public string Category { get; set; } = "General";
        public string CategoryDescription { get; set; } = string.Empty;
        public string LibraryGroup { get; set; } = "General";
        public bool IncludeInCopilotLibrary { get; set; } = true;
        public bool IncludeInAssessmentSuite { get; set; } = true;
        public List<string> LibrarySurfaces { get; set; } = ["default"];
        public int SortOrder { get; set; }
        public List<CopilotChatMessage> SeedHistory { get; set; } = new();

        public CopilotChatMode? ExpectedMode { get; set; }
        public CopilotIntentKind? ExpectedIntent { get; set; }
        public string? ExpectedToolKey { get; set; }
        public bool? RequiresRecordContext { get; set; }

        public string ExpectedBehaviorSummary
        {
            get
            {
                var parts = new List<string>();

                if (ExpectedIntent.HasValue)
                {
                    parts.Add($"Intent: {ExpectedIntent.Value}");
                }

                if (ExpectedMode.HasValue)
                {
                    parts.Add($"Mode: {ExpectedMode.Value}");
                }

                if (!string.IsNullOrWhiteSpace(ExpectedToolKey))
                {
                    parts.Add($"Tool: {ExpectedToolKey}");
                }

                if (RequiresRecordContext.HasValue)
                {
                    parts.Add(RequiresRecordContext.Value ? "Context: required" : "Context: optional");
                }

                return parts.Count > 0 ? string.Join(" | ", parts) : "General behavior";
            }
        }

        public bool SupportsSurface(string? surface)
        {
            var requestedSurface = string.IsNullOrWhiteSpace(surface) ? "default" : surface.Trim();

            if (LibrarySurfaces == null || LibrarySurfaces.Count == 0)
            {
                return string.Equals(requestedSurface, "default", StringComparison.OrdinalIgnoreCase);
            }

            return LibrarySurfaces.Any(value =>
                string.Equals(value, requestedSurface, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));
        }
    }

    public class CopilotAssessmentCaseGroup
    {
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<CopilotAssessmentCase> Cases { get; set; } = new();
    }

    public class CopilotAssessmentLabViewModel
    {
        public List<CopilotAssessmentCaseGroup> CaseGroups { get; set; } = new();
        public List<CopilotPromptGroup> CopilotSampleGroups { get; set; } = new();
        public int TotalCases => CaseGroups.Sum(group => group.Cases.Count);
    }

    /// <summary>
    /// Result of a single assessment execution.
    /// </summary>
    public class CopilotAssessmentResult
    {
        public CopilotAssessmentCase Case { get; set; } = new();
        public CopilotChatResponse? ActualResponse { get; set; }
        public string FailureReason { get; set; } = string.Empty;

        public bool IsException => !string.IsNullOrWhiteSpace(FailureReason);
        public string ActualIntent => ActualResponse?.ExecutionDetails?.DetectedIntent ?? string.Empty;
        public string ActualMode => ActualResponse?.ResponseMode.ToString() ?? string.Empty;
        public string ActualTool => ActualResponse?.UsedTool ?? string.Empty;
        public long LatencyMs => ActualResponse?.ExecutionDetails?.TotalElapsedMs ?? 0;
        public bool HasRecordContext => DetectRecordContext();

        public bool PassedMode =>
            !Case.ExpectedMode.HasValue ||
            string.Equals(ActualMode, Case.ExpectedMode.Value.ToString(), StringComparison.OrdinalIgnoreCase);

        public bool PassedIntent =>
            !Case.ExpectedIntent.HasValue ||
            string.Equals(ActualIntent, Case.ExpectedIntent.Value.ToString(), StringComparison.OrdinalIgnoreCase);

        public bool PassedTool =>
            string.IsNullOrWhiteSpace(Case.ExpectedToolKey) ||
            string.Equals(ActualTool, Case.ExpectedToolKey, StringComparison.OrdinalIgnoreCase);

        public bool PassedContext =>
            !Case.RequiresRecordContext.HasValue ||
            Case.RequiresRecordContext.Value == HasRecordContext;

        public bool IsSuccess => !IsException && PassedMode && PassedIntent && PassedTool && PassedContext;

        public string Detail
        {
            get
            {
                if (IsException)
                {
                    return FailureReason;
                }

                var issues = new List<string>();

                if (!PassedMode && Case.ExpectedMode.HasValue)
                {
                    issues.Add($"mode expected {Case.ExpectedMode.Value}, got {ActualMode}");
                }

                if (!PassedIntent && Case.ExpectedIntent.HasValue)
                {
                    issues.Add($"intent expected {Case.ExpectedIntent.Value}, got {ActualIntent}");
                }

                if (!PassedTool && !string.IsNullOrWhiteSpace(Case.ExpectedToolKey))
                {
                    issues.Add($"tool expected {Case.ExpectedToolKey}, got {ActualTool}");
                }

                if (!PassedContext && Case.RequiresRecordContext.HasValue)
                {
                    issues.Add(Case.RequiresRecordContext.Value ? "record context missing" : "unexpected record context");
                }

                return issues.Count > 0
                    ? string.Join(" | ", issues)
                    : "Matched expected behavior.";
            }
        }

        public string AnswerPreview
        {
            get
            {
                var answer = ActualResponse?.Answer ?? string.Empty;
                if (string.IsNullOrWhiteSpace(answer))
                {
                    return string.Empty;
                }

                answer = answer.ReplaceLineEndings(" ").Trim();
                return answer.Length <= 220 ? answer : $"{answer[..220]}...";
            }
        }

        private bool DetectRecordContext()
        {
            if (ActualResponse == null)
            {
                return false;
            }

            if (ActualResponse.ActionPlan?.TargetTicketId.HasValue == true)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(ActualResponse.DynamicQueryPlan?.TicketNumber))
            {
                return true;
            }

            if (ActualResponse.StructuredQueryResults.Any(result => !string.IsNullOrWhiteSpace(result.Execution.Plan.TicketNumber)))
            {
                return true;
            }

            if (ActualResponse.ExecutionDetails?.QueryPlans?.Any(plan => !string.IsNullOrWhiteSpace(plan.TicketNumber)) == true)
            {
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Aggregated results for an assessment run.
    /// </summary>
    public class CopilotAssessmentReport
    {
        public DateTime RunAt { get; set; } = DateTime.UtcNow;
        public List<CopilotAssessmentResult> Results { get; set; } = new();

        public int TotalCases => Results.Count;
        public int SuccessCount => Results.Count(result => result.IsSuccess);
        public double SuccessRate => TotalCases > 0 ? (double)SuccessCount / TotalCases : 0;
        public long AverageLatencyMs => Results.Count > 0 ? (long)Results.Average(result => result.LatencyMs) : 0;
    }

    public class CopilotAssessmentRunSummaryDto
    {
        public int SummaryId { get; set; }
        public DateTime RunAt { get; set; }
        public int TotalCases { get; set; }
        public int SuccessCount { get; set; }
        public double SuccessRate { get; set; }
        public long AverageLatencyMs { get; set; }
        public List<CopilotAssessmentCaseResultDto> Results { get; set; } = new();
    }

    public class CopilotAssessmentCaseResultDto
    {
        public string Id { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;
        public string ExpectedBehavior { get; set; } = string.Empty;
        public string ActualMode { get; set; } = string.Empty;
        public string ActualIntent { get; set; } = string.Empty;
        public string ActualTool { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string AnswerPreview { get; set; } = string.Empty;
        public long LatencyMs { get; set; }
        public bool IsSuccess { get; set; }
    }
}
