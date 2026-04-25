using AISupportAnalysisPlatform.Enums;
using System.Collections.Generic;
using System.Linq;
using AISupportAnalysisPlatform.Models;
using AISupportAnalysisPlatform.Services.Infrastructure;
using AISupportAnalysisPlatform.Constants;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class AiReviewSignalService : IAiReviewSignalService
    {
        private readonly ILocalizationService _localizer;

        public AiReviewSignalService(ILocalizationService localizer)
        {
            _localizer = localizer;
        }

        public bool NeedsReview(TicketAiAnalysis analysis, int totalRunCount, string language = "English")
        {
            return CalculateReviewReasons(analysis, totalRunCount, language).Any();
        }

        public List<string> CalculateReviewReasons(TicketAiAnalysis analysis, int totalRunCount, string language = "English")
        {
            var reasons = new List<string>();
            var actualCat = analysis.Ticket?.Category?.Name ?? _localizer.Get(nameof(SystemStrings.None), language);
            var actualLevel = analysis.Ticket?.Priority?.Level ?? 0;
            var aiLevelStr = analysis.SuggestedPriority ?? TicketPriorityNames.Medium;
            var aiLevel = aiLevelStr == TicketPriorityNames.High ? 3 : (aiLevelStr == TicketPriorityNames.Medium ? 2 : 1);

            if (analysis.ConfidenceLevel == AiConfidenceLevel.Low) 
                reasons.Add(_localizer.Get("LowConfidence", language));
            
            if (analysis.SuggestedClassification == nameof(SystemStrings.Unknown)) 
                reasons.Add(_localizer.Get("UnknownClassification", language));
            
            if (analysis.SuggestedClassification != nameof(SystemStrings.Unknown) && analysis.SuggestedClassification != actualCat) 
                reasons.Add(_localizer.Get("ClassificationMismatch", language));
            
            if (aiLevel > actualLevel) 
                reasons.Add(_localizer.Get("AiPriorityHigher", language));
            
            if (totalRunCount > 1) 
                reasons.Add(_localizer.Get("MultipleRuns", language));
            
            return reasons;
        }
    }
}
