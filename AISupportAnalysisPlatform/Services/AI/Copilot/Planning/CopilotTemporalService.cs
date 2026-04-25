using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;
using System.Globalization;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotTemporalService
    {
        private readonly string _culture;

        public CopilotTemporalService(string culture = Culture.English)
        {
            _culture = culture;
        }

        public (DateTime? Start, DateTime? End) ParseRange(string input, DateTime? referenceDate = null)
        {
            if (string.IsNullOrWhiteSpace(input)) return (null, null);

            var refDate = referenceDate ?? DateTime.UtcNow;
            var results = DateTimeRecognizer.RecognizeDateTime(input, _culture, refTime: refDate);

            foreach (var result in results)
            {
                if (result.TypeName.StartsWith("datetimeV2"))
                {
                    var values = (List<Dictionary<string, string>>)result.Resolution["values"];
                    foreach (var val in values)
                    {
                        if (val.TryGetValue("start", out var startStr) && val.TryGetValue("end", out var endStr))
                        {
                            if (DateTime.TryParse(startStr, out var start) && DateTime.TryParse(endStr, out var end))
                            {
                                return (start, end);
                            }
                        }
                        else if (val.TryGetValue("value", out var singleVal))
                        {
                            if (DateTime.TryParse(singleVal, out var date))
                            {
                                // If it's a single date, we assume the whole day
                                return (date.Date, date.Date.AddDays(1));
                            }
                        }
                    }
                }
            }

            return (null, null);
        }
    }
}
