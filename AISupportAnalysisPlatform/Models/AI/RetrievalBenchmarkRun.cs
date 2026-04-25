using System;
using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models.AI
{
    public class RetrievalBenchmarkRun
    {
        [Key]
        public int Id { get; set; }
        public DateTime RunOnUtc { get; set; }
        
        public int TotalCases { get; set; }
        public int EvaluatedCases { get; set; }
        public int HitCases { get; set; }
        public double HitRate { get; set; }
        
        public string Version { get; set; } = string.Empty;
        
        // Stores the configuration used (Weights/Toggles) as JSON
        public string SettingsJson { get; set; } = string.Empty;

        // Stores the individual case results (id, isHit) as JSON
        public string ResultsJson { get; set; } = string.Empty;
    }
}
