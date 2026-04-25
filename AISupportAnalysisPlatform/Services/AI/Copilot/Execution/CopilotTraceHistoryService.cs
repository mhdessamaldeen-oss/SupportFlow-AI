using System.Text.Json;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotTraceHistoryService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly ILogger<CopilotTraceHistoryService> _logger;

        public CopilotTraceHistoryService(
            IDbContextFactory<ApplicationDbContext> contextFactory,
            ILogger<CopilotTraceHistoryService> logger)
        {
            _contextFactory = contextFactory;
            _logger = logger;
        }

        public async Task<int?> SaveAsync(
            CopilotChatResponse response,
            long elapsedMs,
            string intent,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var historyRecord = new CopilotTraceHistory
                {
                    Question = response.Question,
                    Answer = response.Answer,
                    CreatedAt = DateTime.UtcNow,
                    ModelName = response.ModelName,
                    TotalElapsedMs = elapsedMs,
                    Intent = intent,
                    ExecutionDetailsJson = JsonSerializer.Serialize(response.ExecutionDetails, new JsonSerializerOptions { WriteIndented = false })
                };

                using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                context.CopilotTraceHistories.Add(historyRecord);
                await context.SaveChangesAsync(cancellationToken);
                return historyRecord.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist copilot trace history.");
                return null;
            }
        }
    }
}
