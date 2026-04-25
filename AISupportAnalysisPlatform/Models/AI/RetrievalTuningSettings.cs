namespace AISupportAnalysisPlatform.Models.AI
{
    public class RetrievalTuningSettings
    {
        // Hybrid Weights
        public float VectorWeight { get; set; } = 0.82f;
        public float LexicalWeight { get; set; } = 0.18f;
        public float MixedVectorWeight { get; set; } = 0.88f;
        public float MixedLexicalWeight { get; set; } = 0.12f;

        // Thresholds
        public float BaseThreshold { get; set; } = 0.18f;
        
        // Language Boost
        public float SameLanguageBoost { get; set; } = 0.03f;
        public float MixedScriptBoost { get; set; } = 0.015f;

        // Noisy Fields (Toggle which fields are included in embedding)
        public bool IncludeComments { get; set; } = true;
        public bool IncludeAttachments { get; set; } = true;
        public bool IncludeTechnicalAssessment { get; set; } = true;
        public bool IncludeResolutionSummary { get; set; } = true;
        public bool IncludeMetadata { get; set; } = true; // Category, Priority, etc.
    }
}
