using AISupportAnalysisPlatform.Enums;

namespace AISupportAnalysisPlatform.Services.AI.Providers
{
    /// <summary>
    /// Unified result returned by all AI providers.
    /// Replaces the old engine-specific result shape with a provider-agnostic contract.
    /// </summary>
    public class AiProviderResult
    {
        public bool Success { get; set; }
        public string ResponseText { get; set; } = "";
        public string? Error { get; set; }

        /// <summary>
        /// The provider type that generated this result.
        /// </summary>
        public AiProviderType ProviderType { get; set; }
    }
}
