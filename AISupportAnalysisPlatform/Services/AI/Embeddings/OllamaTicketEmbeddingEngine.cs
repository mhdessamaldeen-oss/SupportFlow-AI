using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class OllamaTicketEmbeddingEngine : ITicketEmbeddingEngine
    {
        private readonly ILogger<OllamaTicketEmbeddingEngine> _logger;
        private readonly HttpClient _httpClient;
        
        public string ModelName => "bge-m3";

        public OllamaTicketEmbeddingEngine(ILogger<OllamaTicketEmbeddingEngine> logger)
        {
            _logger = logger;
            // Connect to local dockerized Ollama
            _httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:11434/") };
        }

        public float[] GenerateEmbedding(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();

            try
            {
                var requestBody = new
                {
                    model = "bge-m3",
                    input = text
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                
                // Synchronous wait is acceptable here since the embedding queue runs on a background thread.
                var response = _httpClient.PostAsync("api/embed", content).GetAwaiter().GetResult();
                
                response.EnsureSuccessStatusCode();

                var jsonResponse = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var result = JsonSerializer.Deserialize<OllamaEmbedResponse>(jsonResponse);

                if (result?.Embeddings != null && result.Embeddings.Count > 0)
                {
                    return result.Embeddings[0].ToArray();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate embedding from Ollama for text using bge-m3.");
            }

            return Array.Empty<float>();
        }

        private class OllamaEmbedResponse
        {
            [JsonPropertyName("embeddings")]
            public List<List<float>> Embeddings { get; set; } = new();
        }
    }
}
