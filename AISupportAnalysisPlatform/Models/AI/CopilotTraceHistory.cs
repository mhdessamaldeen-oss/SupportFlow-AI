using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models.AI
{
    public class CopilotTraceHistory
    {
        public int Id { get; set; }

        public string? UserId { get; set; }
        public ApplicationUser? User { get; set; }


        [Required]
        public string Question { get; set; } = string.Empty;

        public string? Answer { get; set; }

        public string ExecutionDetailsJson { get; set; } = "{}";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public string? ModelName { get; set; }
        public long TotalElapsedMs { get; set; }
        
        // Simplified search keywords for the archive page
        public string? Intent { get; set; }
    }
}
