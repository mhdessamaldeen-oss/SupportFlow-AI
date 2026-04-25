using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// In-app multilingual hashing vectorizer for ticket similarity.
    /// Keeps embeddings local and deterministic without depending on the active chat provider.
    /// </summary>
    public class LocalTicketEmbeddingEngine : ITicketEmbeddingEngine
    {
        private const int VectorSize = 384;
        private static readonly Regex TokenRegex = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

        public string ModelName => "local-multilingual-hash-v1";

        public float[] GenerateEmbedding(string text)
        {
            var vector = new float[VectorSize];
            if (string.IsNullOrWhiteSpace(text))
            {
                return vector;
            }

            var normalized = NormalizeText(text);
            var tokens = TokenRegex.Matches(normalized)
                .Select(match => match.Value)
                .Where(token => token.Length >= 2)
                .ToList();

            foreach (var token in tokens)
            {
                AddWeightedFeature(vector, token, 1.8f);

                foreach (var gram in GetCharacterNgrams(token))
                {
                    AddWeightedFeature(vector, gram, 0.65f);
                }
            }

            NormalizeVector(vector);
            return vector;
        }

        private static void AddWeightedFeature(float[] vector, string feature, float weight)
        {
            var hash = GetStableHash(feature);
            var index = Math.Abs(hash % VectorSize);
            var sign = ((hash >> 1) & 1) == 0 ? 1f : -1f;
            vector[index] += sign * weight;
        }

        private static IEnumerable<string> GetCharacterNgrams(string token)
        {
            if (token.Length <= 3)
            {
                yield return token;
                yield break;
            }

            for (var i = 0; i <= token.Length - 3; i++)
            {
                yield return token.Substring(i, 3);
            }
        }

        private static string NormalizeText(string text)
        {
            var sb = new StringBuilder(text.Length);

            foreach (var ch in text.Normalize(NormalizationForm.FormKC))
            {
                if (char.IsControl(ch))
                {
                    continue;
                }

                var lowered = char.ToLower(ch, CultureInfo.InvariantCulture);
                sb.Append(lowered switch
                {
                    'أ' or 'إ' or 'آ' => 'ا',
                    'ة' => 'ه',
                    'ى' => 'ي',
                    _ => lowered
                });
            }

            return sb.ToString();
        }

        private static int GetStableHash(string value)
        {
            unchecked
            {
                var hash = 23;
                foreach (var ch in value)
                {
                    hash = (hash * 31) + ch;
                }

                return hash;
            }
        }

        private static void NormalizeVector(float[] vector)
        {
            double magnitude = 0;
            foreach (var value in vector)
            {
                magnitude += value * value;
            }

            if (magnitude <= 0)
            {
                return;
            }

            var scale = (float)(1.0 / Math.Sqrt(magnitude));
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] *= scale;
            }
        }
    }
}
