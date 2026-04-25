using System.Text.RegularExpressions;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using FuzzierSharp;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// High-precision preprocessor for the AI Copilot.
    /// Orchestrates text normalization, entity extraction, and structural intent discovery.
    /// </summary>
    public class CopilotQuestionPreprocessor : ICopilotQuestionPreprocessor
    {
        private static readonly Regex TicketPattern = new(@"\b([A-Za-z]+-\d+|\d{4,})\b", RegexOptions.Compiled);
        private static readonly Regex HyphenCleaner = new(@"(?<=\p{L})-(?=\p{L})", RegexOptions.Compiled);
        private static readonly string[] StrongMultiPartSeparators = ["also", "then", "plus", "ثم", "كذلك"];

        private readonly CopilotHeuristicCatalog _catalog;
        private readonly (Regex Pattern, bool Anchored)[] _intentPatterns;
        private readonly IReadOnlyList<(string Source, string Replacement)> _synonyms;
        private readonly IReadOnlyList<string> _fuzzyDictionary;

        public CopilotQuestionPreprocessor(CopilotHeuristicCatalog catalog)
        {
            _catalog = catalog;

            // Pre-compile compiled regex signals for social and structural markers
            _intentPatterns = new (Regex, bool)[]
            {
                (BuildRegex(catalog.GreetingPhrases.Concat(catalog.CapabilityQuestions), true), true),   // 0: Greeting / capability
                (BuildRegex(catalog.FollowUpPhrases, false), false) // 1: Follow-up
            };

            // Order synonyms by length (descending) to prevent partial matching conflicts
            _synonyms = catalog.SynonymMap
                .Select(x => (Source: x.Key.ToLowerInvariant(), Replacement: x.Value.ToLowerInvariant()))
                .OrderByDescending(x => x.Source.Length)
                .ToList();

            // Prepare fuzzy dictionary for typo-correction (long alpha-terms only)
            _fuzzyDictionary = catalog.AnalyticsPhrases
                .Concat(catalog.TicketDomainPhrases)
                .Concat(catalog.KnowledgePhrases)
                .Select(t => t.ToLowerInvariant().Trim())
                .Where(t => t.Length >= 5 && !t.Contains(' ') && t.Any(c => c is >= 'a' and <= 'z'))
                .Distinct()
                .ToList();
        }

        public CopilotQuestionContext Process(CopilotChatRequest request, CopilotConversationContext? context = null)
        {
            var raw = request.Question?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return new CopilotQuestionContext { OriginalQuestion = raw };

            var trace = new List<string>();
            var normalized = Normalize(raw, context, trace);
            
            var ctx = new CopilotQuestionContext
            {
                OriginalQuestion = raw,
                NormalizedQuestion = normalized,
                PreprocessingTrace = trace,
                Language = raw.Any(c => c >= 0x0600 && c <= 0x06FF) ? "ar" : "en",
                ConversationContext = context ?? new(),
            };

            ExtractEntities(ctx, raw);
            
            ctx.LooksMultiPart = HasMultipleClauses(raw, normalized);
            
            ctx.IsPotentiallyUnsafe = _catalog.InjectionPatterns.Any(p => normalized.Contains(p, StringComparison.OrdinalIgnoreCase));
            ctx.LooksLikeGreeting =
                _intentPatterns[0].Pattern.IsMatch(normalized) ||
                _catalog.CapabilityQuestions.Any(phrase => normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase));
            ctx.IsFollowUpQuestion = context?.HasPriorContext == true && (context.IsFollowUpCandidate || _intentPatterns[1].Pattern.IsMatch(normalized));
            
            // Structural signals (Hints for the classifier/routing layer)
            ctx.LooksLikeDataQuestion = _catalog.AnalyticsPhrases.Any(p => normalized.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
                                        _catalog.TicketDomainPhrases.Any(p => normalized.Contains(p, StringComparison.OrdinalIgnoreCase));
            
            ctx.LooksLikeGuidanceQuestion = _catalog.KnowledgePhrases.Any(p => normalized.Contains(p, StringComparison.OrdinalIgnoreCase));
            ctx.LooksLikeComplexDataQuestion = false;

            FinalizeContext(ctx, raw);

            return ctx;
        }

        private void ExtractEntities(CopilotQuestionContext ctx, string raw)
        {
            var ticketMatch = TicketPattern.Match(raw);
            if (ticketMatch.Success)
            {
                ctx.TicketNumber = ticketMatch.Groups[1].Value.Trim();
                ctx.HasTicketReference = true;
                ctx.SignalsFound["Ticket"] = $"Resolved to {ctx.TicketNumber} (Direct)";
            }
            else if (ctx.IsFollowUpQuestion)
            {
                ctx.TicketNumber = ctx.ConversationContext.LastTicketNumber ?? ctx.ConversationContext.PreviousQueryPlan?.TicketNumber ?? "";
                ctx.HasTicketReference = !string.IsNullOrWhiteSpace(ctx.TicketNumber);
                if (ctx.HasTicketReference) ctx.SignalsFound["Ticket"] = $"Resolved to {ctx.TicketNumber} (Inferred from context)";
            }
        }



        private void FinalizeContext(CopilotQuestionContext ctx, string raw)
        {
            ctx.SearchText = ctx.HasTicketReference 
                ? Regex.Replace(ctx.NormalizedQuestion, $@"\b{Regex.Escape(ctx.TicketNumber)}\b", "", RegexOptions.IgnoreCase).Trim() 
                : ctx.NormalizedQuestion;

            ctx.QueryParts = raw.Split(['?', '.', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        }

        private string Normalize(string input, CopilotConversationContext? context = null, List<string>? trace = null)
        {
            // A. Clean and replace whole phrase synonyms
            var text = CopilotLexicalMatcherService.Normalize(HyphenCleaner.Replace(input.ToLowerInvariant(), " "));
            
            // Build dynamic fuzzy candidates from context (Bias toward recent entities)
            var dynamicCandidates = new List<string>(_fuzzyDictionary);
            if (context != null && !string.IsNullOrWhiteSpace(context.LastTicketNumber))
            {
                var lastTicket = context.LastTicketNumber.ToLowerInvariant();
                if (!dynamicCandidates.Contains(lastTicket)) dynamicCandidates.Add(lastTicket);
            }

            foreach (var (source, replacement) in _synonyms)
            {
                var before = text;
                text = Regex.Replace(text, $@"(?<!\S){Regex.Escape(source)}(?!\S)", replacement);
                if (text != before && trace != null)
                {
                    trace.Add($"Synonym: '{source}' -> '{replacement}'");
                }
            }

            // B. Token-level fuzzy typo correction
            var result = new List<string>();
            foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var corrected = token;
                
                // Only fuzzy match terms that are likely words or ticket-like identifiers
                if (token.Length >= 4 && (token.Any(c => c is >= 'a' and <= 'z') || token.Contains('-')))
                {
                    var match = dynamicCandidates
                        .Where(c => Math.Abs(c.Length - token.Length) <= 2)
                        .Select(c => (Candidate: c, Score: Fuzz.WeightedRatio(token, c)))
                        .Where(x => x.Score >= 82) // Sharpened threshold (Senior grade)
                        .OrderByDescending(x => x.Score)
                        .FirstOrDefault();

                    if (match.Candidate != null && match.Candidate != token) 
                    {
                        corrected = match.Candidate;
                        trace?.Add($"Contextual Fuzzy Fix: '{token}' -> '{corrected}' (Score: {match.Score})");
                    }
                }
                result.Add(corrected);
            }

            return CopilotLexicalMatcherService.Normalize(string.Join(' ', result));
        }



        private static Regex BuildRegex(IEnumerable<string> phrases, bool anchored)
        {
            var pattern = string.Join("|", phrases.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Regex.Escape(p.Trim())).Distinct());
            if (string.IsNullOrWhiteSpace(pattern)) return new Regex("$a", RegexOptions.Compiled);
            
            var final = anchored ? $@"^(?:{pattern})(?:[\s\p{{P}}]+.*)?$" : $@"\b(?:{pattern})\b";
            return new Regex(final, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        private static bool HasMultipleClauses(string raw, string normalized)
        {
            if (raw.Split(['?', '.', '\n'], StringSplitOptions.RemoveEmptyEntries).Length > 1)
            {
                return true;
            }

            if (StrongMultiPartSeparators.Any(separator => normalized.Contains($" {separator} ", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }
    }
}
