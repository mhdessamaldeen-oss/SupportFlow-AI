namespace AISupportAnalysisPlatform.Services.AI;

public interface ITicketEmbeddingEngine
{
    string ModelName { get; }
    float[] GenerateEmbedding(string text);
}
