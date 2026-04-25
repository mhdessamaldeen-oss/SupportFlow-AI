using AISupportAnalysisPlatform.Enums;
using System.Text;
using System.Text.Json;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Constants;
using Microsoft.Extensions.Options;

namespace AISupportAnalysisPlatform.Services.AI.Providers
{
    /// <summary>
    /// AI provider specifically for Google Gemini (AI Studio / Vertex AI).
    /// Handles Gemini's native JSON format for both generation and embeddings.
    /// </summary>
    public class GeminiAiProvider : IAiProvider
    {
        private readonly CloudProviderOptions _configOptions;
        private readonly ILogger<GeminiAiProvider> _logger;
        private readonly IServiceProvider _serviceProvider;

        public AiProviderType ProviderType => AiProviderType.Cloud; // Reusing Cloud slot or can add Gemini type

        public GeminiAiProvider(
            IOptions<AiProviderSettings> settings,
            ILogger<GeminiAiProvider> logger,
            IServiceProvider serviceProvider)
        {
            _configOptions = settings.Value.Cloud;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public string ModelName => GetDbSetting(SettingKeys.GeminiModel) ?? _configOptions.Model ?? "gemini-1.5-flash";
        private string GetApiKey() => GetDbSetting(SettingKeys.GeminiApiKey) ?? _configOptions.ApiKey;
        private double GetTemperature() => double.TryParse(GetDbSetting(SettingKeys.GeminiTemperature), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : _configOptions.Temperature;
        private int GetMaxTokens() => int.TryParse(GetDbSetting(SettingKeys.GeminiMaxTokens), out var value) ? value : _configOptions.MaxTokens;

        public async Task<AiProviderResult> GenerateAsync(string prompt)
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
                return new AiProviderResult { Success = false, Error = "Gemini API key is missing.", ProviderType = ProviderType };

            var model = ModelName;
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

            try
            {
                using var client = new HttpClient();
                var requestBody = new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = new { temperature = GetTemperature(), maxOutputTokens = GetMaxTokens() }
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    return new AiProviderResult { Success = false, Error = $"Gemini error: {error}", ProviderType = ProviderType };
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

                return new AiProviderResult { Success = true, ResponseText = text ?? "", ProviderType = ProviderType };
            }
            catch (Exception ex)
            {
                return new AiProviderResult { Success = false, Error = ex.Message, ProviderType = ProviderType };
            }
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return Array.Empty<float>();

            // Gemini embedding models are different from chat models
            var model = ModelName.Contains("embedding") ? ModelName : "text-embedding-004";
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:embedContent?key={apiKey}";

            try
            {
                using var client = new HttpClient();
                var requestBody = new { content = new { parts = new[] { new { text = text } } } };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);

                if (!response.IsSuccessStatusCode) return Array.Empty<float>();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var values = doc.RootElement.GetProperty("embedding").GetProperty("values");

                var result = new float[values.GetArrayLength()];
                for (int i = 0; i < result.Length; i++) result[i] = (float)values[i].GetDouble();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gemini embedding failed");
                return Array.Empty<float>();
            }
        }

        public async Task<(bool IsValid, string? ErrorMessage)> ValidateConfigurationAsync()
        {
            if (string.IsNullOrWhiteSpace(GetApiKey())) return (false, "Missing Gemini API Key.");
            return (true, null);
        }

        private string? GetDbSetting(string key)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return db.SystemSettings.FirstOrDefault(s => s.Key == key)?.Value;
        }
    }
}
