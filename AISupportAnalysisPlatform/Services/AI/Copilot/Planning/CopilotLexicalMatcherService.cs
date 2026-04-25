using FuzzierSharp;
using System.Text.RegularExpressions;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Shared lexical matcher for known business values.
    /// Match order is:
    /// 1. exact normalized phrase
    /// 2. labeled exact phrase
    /// 3. controlled fuzzy match
    ///
    /// Fuzzy matching is intentionally strict. If two candidates are too close,
    /// the matcher returns no value so the caller can avoid guessing.
    /// </summary>
    public class CopilotLexicalMatcherService
    {
        public string FindBestValue(string normalizedQuestion, IReadOnlyCollection<string> values)
            => FindBestValue(normalizedQuestion, values, []);

        public string FindBestValue(string normalizedQuestion, IReadOnlyCollection<string> values, IReadOnlyCollection<string> labels)
        {
            if (string.IsNullOrWhiteSpace(normalizedQuestion) || values.Count == 0)
            {
                return "";
            }

            var candidates = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new LexicalCandidate(value, Normalize(value)))
                .OrderByDescending(candidate => candidate.Normalized.Length)
                .ToList();

            var exact = FindExactMatch(normalizedQuestion, candidates, labels);
            if (!string.IsNullOrWhiteSpace(exact))
            {
                return exact;
            }

            return FindFuzzyMatch(normalizedQuestion, candidates);
        }

        private static string FindExactMatch(
            string normalizedQuestion,
            IReadOnlyCollection<LexicalCandidate> candidates,
            IReadOnlyCollection<string> labels)
        {
            foreach (var candidate in candidates)
            {
                if (ContainsPhrase(normalizedQuestion, candidate.Normalized))
                {
                    return candidate.Original;
                }

                foreach (var label in labels)
                {
                    var normalizedLabel = Normalize(label);
                    if (ContainsPhrase(normalizedQuestion, $"{normalizedLabel} {candidate.Normalized}") ||
                        ContainsPhrase(normalizedQuestion, $"{candidate.Normalized} {normalizedLabel}") ||
                        ContainsPhrase(normalizedQuestion, $"with {normalizedLabel} {candidate.Normalized}"))
                    {
                        return candidate.Original;
                    }
                }
            }

            return "";
        }

        private static string FindFuzzyMatch(string normalizedQuestion, IReadOnlyCollection<LexicalCandidate> candidates)
        {
            var windows = BuildSlidingWindows(normalizedQuestion);
            var scored = new List<(LexicalCandidate Candidate, double Score)>();

            foreach (var candidate in candidates)
            {
                var bestScore = 0d;
                foreach (var window in windows)
                {
                    if (!LooksComparable(window, candidate.Normalized))
                    {
                        continue;
                    }

                    var score = ComputeSimilarity(window, candidate.Normalized);
                    if (score > bestScore)
                    {
                        bestScore = score;
                    }
                }

                if (bestScore >= MinimumSimilarity(candidate.Normalized))
                {
                    scored.Add((candidate, bestScore));
                }
            }

            if (scored.Count == 0)
            {
                return "";
            }

            var ordered = scored
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Candidate.Normalized.Length)
                .ToList();

            var best = ordered[0];
            var second = ordered.Skip(1).FirstOrDefault();
            if (second.Candidate != null && best.Score - second.Score < 0.08)
            {
                return "";
            }

            return best.Candidate.Original;
        }

        private static bool LooksComparable(string input, string candidate)
        {
            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var inputTokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (Math.Abs(inputTokens.Length - candidateTokens.Length) > 1)
            {
                return false;
            }

            if (Math.Abs(input.Length - candidate.Length) > 3)
            {
                return false;
            }

            return char.ToLowerInvariant(input[0]) == char.ToLowerInvariant(candidate[0]);
        }

        private static double ComputeSimilarity(string input, string candidate)
        {
            return Fuzz.WeightedRatio(input, candidate) / 100d;
        }

        private static double MinimumSimilarity(string candidate)
        {
            if (candidate.Length <= 4) return 0.8;
            if (candidate.Length <= 7) return 0.72;
            return 0.68;
        }

        private static List<string> BuildSlidingWindows(string normalizedQuestion)
        {
            var tokens = normalizedQuestion.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var windows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var size = 1; size <= Math.Min(3, tokens.Length); size++)
            {
                for (var index = 0; index <= tokens.Length - size; index++)
                {
                    windows.Add(string.Join(' ', tokens.Skip(index).Take(size)));
                }
            }

            return windows.ToList();
        }

        private static bool ContainsPhrase(string text, string phrase)
            => Regex.IsMatch(text, $@"(?<!\S){Regex.Escape(phrase)}(?!\S)", RegexOptions.IgnoreCase);

        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var cleaned = new string(value
                .Trim()
                .ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) || character == '-'
                    ? character
                    : ' ')
                .ToArray());

            return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private sealed record LexicalCandidate(string Original, string Normalized);
    }
}
