using AISupportAnalysisPlatform.Models.AI;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotEvaluationEngine
    {
        private readonly IHostEnvironment _environment;
        private readonly ILogger<CopilotEvaluationEngine> _logger;
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        private readonly SemaphoreSlim _lock = new(1, 1);

        public CopilotEvaluationEngine(IHostEnvironment environment, ILogger<CopilotEvaluationEngine> logger)
        {
            _environment = environment;
            _logger = logger;
        }

        public string StorePath => Path.Combine(_environment.ContentRootPath, "App_Data", "copilot_evaluations.json");

        public async Task<List<CopilotEvaluationEntry>> LoadAsync()
        {
            var storePath = StorePath;
            if (!File.Exists(storePath))
            {
                return new List<CopilotEvaluationEntry>();
            }

            try
            {
                await using var stream = File.OpenRead(storePath);
                return await JsonSerializer.DeserializeAsync<List<CopilotEvaluationEntry>>(stream) ?? new List<CopilotEvaluationEntry>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Copilot evaluation store.");
                return new List<CopilotEvaluationEntry>();
            }
        }

        public async Task SaveAsync(CopilotEvaluationEntry entry)
        {
            await _lock.WaitAsync();
            try
            {
                var entries = await LoadAsync();
                var existing = entries.FirstOrDefault(e => e.TicketId == entry.TicketId);
                if (existing != null)
                {
                    entries.Remove(existing);
                }

                entries.Add(entry);
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                await using var stream = File.Create(StorePath);
                await JsonSerializer.SerializeAsync(stream, entries.OrderByDescending(e => e.EvaluatedOnUtc).ToList(), _jsonOptions);
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
