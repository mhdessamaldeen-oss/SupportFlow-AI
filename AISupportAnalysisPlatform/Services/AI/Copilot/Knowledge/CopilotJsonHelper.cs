using System.Text.Json;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Shared JSON parsing utilities used by copilot services that parse structured model output.
    /// Eliminates duplication across intent classification and tool resolution.
    /// </summary>
    internal static class CopilotJsonHelper
    {
        /// <summary>
        /// Extracts the first JSON object from a free-text model response.
        /// </summary>
        public static string ExtractJson(string text)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start >= 0 && end > start ? text[start..(end + 1)] : text;
        }

        public static string GetString(JsonDocument doc, string propertyName, string fallback = "")
            => doc.RootElement.TryGetProperty(propertyName, out var property) ? property.GetString() ?? fallback : fallback;

        public static bool GetBool(JsonDocument doc, string propertyName, bool fallback = false)
            => doc.RootElement.TryGetProperty(propertyName, out var property) 
                ? property.ValueKind == JsonValueKind.True || (property.ValueKind == JsonValueKind.False ? false : fallback)
                : fallback;

        public static double GetDouble(JsonDocument doc, string propertyName, double fallback = 0.0)
            => doc.RootElement.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value) ? value : fallback;

        public static Dictionary<string, string> GetDictionary(JsonDocument doc, string propertyName)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!doc.RootElement.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (var item in property.EnumerateObject())
            {
                var value = item.Value.ValueKind switch
                {
                    JsonValueKind.String => item.Value.GetString(),
                    JsonValueKind.Number => item.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => item.Value.ToString()
                };

                if (!string.IsNullOrWhiteSpace(value))
                {
                    values[item.Name] = value;
                }
            }

            return values;
        }

        public static TEnum GetEnum<TEnum>(JsonDocument doc, string propertyName, TEnum fallback) where TEnum : struct, Enum
            => doc.RootElement.TryGetProperty(propertyName, out var property) &&
               Enum.TryParse<TEnum>(property.GetString(), true, out var parsed)
                ? parsed
                : fallback;

        public static int GetInt(JsonDocument doc, string propertyName, int fallback)
            => doc.RootElement.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : fallback;

        public static List<string> GetStringList(JsonDocument doc, string propertyName)
            => doc.RootElement.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
                ? property.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToList()
                : new();
    }
}
