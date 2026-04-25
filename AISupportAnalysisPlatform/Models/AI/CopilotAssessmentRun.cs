using System;
using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Database entity to persist Copilot stress-test results.
    /// Mirroring the architecture of RetrievalBenchmarkRun for consistency.
    /// </summary>
    public class CopilotAssessmentRun
    {
        [Key]
        public int Id { get; set; }
        
        public DateTime RunOnUtc { get; set; }
        
        public int TotalCases { get; set; }
        public int SuccessCount { get; set; }
        public double SuccessRate { get; set; }
        public long AverageLatencyMs { get; set; }
        
        // JSON storage for granular results and configuration
        public string ResultsJson { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0";
    }
}
