using AISupportAnalysisPlatform.Models;

namespace AISupportAnalysisPlatform.Services.AI;

public interface ISemanticSearchService
{
    Task UpsertTicketEmbeddingAsync(int ticketId);
    Task<List<SemanticSearchMatch>> GetRelatedTicketsAsync(int ticketId, int count = 5, List<int>? statusIds = null, bool includeAllStatuses = false, CancellationToken cancellationToken = default);
    Task<List<SemanticSearchMatch>> SearchSimilarTicketsByTextAsync(string queryText, int count = 5, List<int>? statusIds = null, bool includeAllStatuses = false, CancellationToken cancellationToken = default);
    Task<AISupportAnalysisPlatform.Models.AI.RetrievalTuningSettings> GetTuningSettingsAsync();
    Task UpdateTuningSettingsAsync(AISupportAnalysisPlatform.Models.AI.RetrievalTuningSettings settings);
}
