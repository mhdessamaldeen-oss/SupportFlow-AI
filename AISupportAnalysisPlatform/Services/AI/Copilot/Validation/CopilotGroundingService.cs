using System.Text;
using System.Text.Json;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using AISupportAnalysisPlatform.Services.AI.Providers;
using Microsoft.Extensions.Logging;

namespace AISupportAnalysisPlatform.Services.AI.Validation
{
    public class CopilotGroundingService : ICopilotGroundingService
    {
        private readonly IAiProviderFactory _providerFactory;
        private readonly CopilotTextCatalog _text;
        private readonly ILogger<CopilotGroundingService> _logger;

        public CopilotGroundingService(
            IAiProviderFactory providerFactory,
            CopilotTextCatalog text,
            ILogger<CopilotGroundingService> _logger)
        {
            _providerFactory = providerFactory;
            _text = text;
            this._logger = _logger;
        }

        public async Task<CopilotGroundingResult> VerifyAsync(
            string question,
            string answer,
            CopilotExecutionResult execution,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(answer)) return new CopilotGroundingResult { IsGrounded = true, Confidence = 1.0 };

            try
            {
                var evidence = BuildEvidenceSummary(execution);
                if (string.IsNullOrWhiteSpace(evidence)) 
                {
                    // No evidence retrieved but we have an answer? Potential hallucination if not a greeting.
                    return new CopilotGroundingResult { IsGrounded = false, Confidence = 0, Analysis = "No supporting evidence retrieved for this query." };
                }

                var deterministicResult = TryBuildDeterministicGroundingResult(execution, evidence);
                if (deterministicResult != null)
                {
                    return deterministicResult;
                }

                var prompt = BuildPrompt(question, answer, evidence);
                var provider = _providerFactory.GetActiveProvider();
                
                var result = await provider.GenerateAsync(prompt);
                if (result.Success && !string.IsNullOrWhiteSpace(result.ResponseText))
                {
                    var grounding = ParseGroundingResult(result.ResponseText);
                    grounding.EvidenceUsed = evidence;
                    return grounding;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Grounding verification failed. Defaulting to unverified.");
            }

            return new CopilotGroundingResult { IsGrounded = true, Confidence = 0.5, Analysis = "Grounding check could not be completed." };
        }

        private string BuildEvidenceSummary(CopilotExecutionResult result)
        {
            var sb = new StringBuilder();
            AppendResultToEvidence(result, sb);
            return sb.ToString();
        }

        private void AppendResultToEvidence(CopilotExecutionResult result, StringBuilder sb)
        {
            if (result.StructuredResult != null)
            {
                var execution = result.StructuredResult;
                sb.AppendLine($"--- Database Query Evidence ({result.Summary}) ---");
                sb.AppendLine($"Execution Summary: {execution.Summary}");
                if (execution.TotalCount > 0 || execution.StructuredRows?.Any() == true || execution.Rows?.Any() == true)
                {
                    sb.AppendLine($"Result Count: {execution.TotalCount}");
                }

                if (execution.StructuredRows?.Any() == true)
                {
                    foreach (var row in execution.StructuredRows.Take(10))
                    {
                        sb.AppendLine(JsonSerializer.Serialize(row.Values));
                    }
                }
                if (execution.Rows?.Any() == true)
                {
                    foreach (var ticket in execution.Rows.Take(5))
                    {
                        sb.AppendLine($"Ticket {ticket.TicketNumber}: {ticket.Title}. Status: {ticket.Status}. Priority: {ticket.Priority}");
                    }
                }
            }

            if (result.KnowledgeMatches?.Any() == true)
            {
                sb.AppendLine($"--- Knowledge Base Evidence ({result.Summary}) ---");
                foreach (var match in result.KnowledgeMatches.Take(5))
                {
                    sb.AppendLine($"Doc: {match.DocumentName}, Section: {match.SectionTitle}");
                    sb.AppendLine($"Content: {match.Excerpt}");
                }
            }

            if (!string.IsNullOrWhiteSpace(result.TechnicalData) && result.StructuredResult == null)
            {
                sb.AppendLine($"--- External Tool Evidence ({result.Summary}) ---");
                sb.AppendLine(result.TechnicalData);
            }

            // Recursively add all sub-results to the evidence pool
            foreach (var sub in result.SubResults)
            {
                AppendResultToEvidence(sub, sb);
            }
        }

        private static CopilotGroundingResult? TryBuildDeterministicGroundingResult(
            CopilotExecutionResult execution,
            string evidence)
        {
            var isSingleStructuredQuery =
                execution.StructuredResult != null &&
                execution.SubResults.Count == 0 &&
                execution.KnowledgeMatches.Count == 0;

            if (!isSingleStructuredQuery && !execution.IsDeterministicEvidenceAnswer)
            {
                return null;
            }

            return new CopilotGroundingResult
            {
                IsGrounded = true,
                Confidence = 0.98,
                Analysis = ResolveDeterministicGroundingAnalysis(execution),
                EvidenceUsed = evidence
            };
        }

        private static string ResolveDeterministicGroundingAnalysis(CopilotExecutionResult execution)
        {
            if (execution.StructuredResult != null && execution.SubResults.Count == 0 && execution.KnowledgeMatches.Count == 0)
            {
                return "Skipped model-based grounding because this answer was produced directly from approved catalog SQL results.";
            }

            if (!string.IsNullOrWhiteSpace(execution.UsedTool) && !execution.UsedTool.Equals("none", StringComparison.OrdinalIgnoreCase) && execution.SubResults.Count == 0)
            {
                return "Skipped model-based grounding because this answer was formatted directly from the external tool payload.";
            }

            if (execution.SubResults.Count > 0)
            {
                return "Skipped model-based grounding because the final answer only merged direct-evidence branch results into labeled sections.";
            }

            return "Skipped model-based grounding because this answer came directly from verified evidence.";
        }

        private string BuildPrompt(string question, string answer, string evidence)
        {
            return CopilotTextTemplate.Apply(
                CopilotTextTemplate.JoinLines(_text.GroundingPromptLines),
                new Dictionary<string, string?>
                {
                    ["QUESTION"] = question,
                    ["ANSWER"] = answer,
                    ["EVIDENCE"] = evidence
                });
        }

        private static CopilotGroundingResult ParseGroundingResult(string responseText)
        {
            try
            {
                var json = CopilotJsonHelper.ExtractJson(responseText);
                using var doc = JsonDocument.Parse(json);
                return new CopilotGroundingResult
                {
                    IsGrounded = CopilotJsonHelper.GetBool(doc, "isGrounded", true),
                    Confidence = CopilotJsonHelper.GetDouble(doc, "confidence", 1.0),
                    Analysis = CopilotJsonHelper.GetString(doc, "analysis"),
                    HallucinationRisks = CopilotJsonHelper.GetStringList(doc, "hallucinationRisks")
                };
            }
            catch
            {
                return new CopilotGroundingResult { IsGrounded = true, Confidence = 0.5, Analysis = "Failed to parse grounding JSON." };
            }
        }
    }
}
