using Microsoft.AspNetCore.Mvc;
using AISupportAnalysisPlatform.Services.AI.Pipeline;
using AISupportAnalysisPlatform.Models.AI;
using System.Text;

namespace AISupportAnalysisPlatform.Controllers.AI
{
    [ApiController]
    [Route("api/test-pipeline")]
    public class TestPipelineController : ControllerBase
    {
        private readonly AnalyticsPipeline _pipeline;

        public TestPipelineController(AnalyticsPipeline pipeline)
        {
            _pipeline = pipeline;
        }

        [HttpGet]
        public async Task<IActionResult> RunTests()
        {
            var questions = new[]
            {
                "How many open critical tickets are in the Finance entity from the last 7 days?",
                "What is the average resolution time for all tickets resolved this month?",
                "Show me a breakdown of tickets by entity for the Support department.",
                // TOUGH TESTS
                "I want a list of users, including their first and last name, sorted by how many tickets they have resolved.",
                "Compare the number of critical tickets created vs closed in the IT department over the last 30 days.",
                "Which entity has the highest SLA breach rate, and how many open tickets do they currently have?",
                "Give me a detailed breakdown of tickets grouped by both priority and status for the last quarter.",
                "How many system settings are there, and list all their keys?"
            };

            var sb = new StringBuilder();
            sb.AppendLine("=== ANALYTICS PIPELINE DEEP-TEST ===");

            foreach (var q in questions)
            {
                sb.AppendLine($"\n[QUESTION]: {q}");
                try
                {
                    var result = await _pipeline.ExecuteAsync(q);
                    if (result == null)
                    {
                        sb.AppendLine("[RESULT]: NULL (Pipeline failed to produce result)");
                        continue;
                    }

                    sb.AppendLine($"[ANSWER]: {result.Answer}");
                    sb.AppendLine($"[SQL]: {result.GeneratedSql}");
                    sb.AppendLine("[INVESTIGATION TREE]:");
                    foreach (var step in result.ExecutionSteps)
                    {
                        sb.AppendLine($"  - {step.Action}: {step.Detail} ({step.Layer})");
                        foreach (var sub in step.SubSteps)
                        {
                            sb.AppendLine($"    * {sub.Action}: {sub.Detail}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[ERROR]: {ex.Message}");
                }
                sb.AppendLine(new string('-', 50));
            }

            return Content(sb.ToString(), "text/plain");
        }
    }
}
