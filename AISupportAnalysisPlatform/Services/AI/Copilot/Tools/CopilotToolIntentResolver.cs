using System.Text.Json;
using System.Text.RegularExpressions;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using AISupportAnalysisPlatform.Services.AI.Providers;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotToolIntentResolver : ICopilotToolIntentResolver
    {
        private readonly CopilotToolRegistryService _toolRegistry;
        private readonly IAiProviderFactory _providerFactory;
        private readonly ILogger<CopilotToolIntentResolver> _logger;
        private readonly HashSet<string> _stopWords;
        private readonly CopilotToolParameterResolverService _parameterResolver;
        private readonly CopilotTextCatalog _text;

        public CopilotToolIntentResolver(
            CopilotToolRegistryService toolRegistry,
            IAiProviderFactory providerFactory,
            CopilotToolParameterResolverService parameterResolver,
            CopilotHeuristicCatalog heuristics,
            CopilotTextCatalog text,
            ILogger<CopilotToolIntentResolver> logger)
        {
            _toolRegistry = toolRegistry;
            _providerFactory = providerFactory;
            _parameterResolver = parameterResolver;
            _stopWords = heuristics.ToolStopWords
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _text = text;
            _logger = logger;
        }

        public async Task<CopilotToolResolution> ResolveAsync(string question, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                return new CopilotToolResolution
                {
                    Reason = _text.ToolResolverEmptyQuestionReason
                };
            }

            var tools = await _toolRegistry.GetEnabledToolsAsync();
            if (!tools.Any())
            {
                return new CopilotToolResolution
                {
                    Reason = _text.ToolResolverNoToolsReason
                };
            }

            var candidates = ScoreCandidates(question, tools, _stopWords)
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ToList();

            if (!candidates.Any())
            {
                return new CopilotToolResolution
                {
                    Reason = _text.ToolResolverNoMatchReason
                };
            }

            var best = candidates[0];
            var secondScore = candidates.Count > 1 ? candidates[1].Score : 0;
            if (best.Score >= 7 && best.Score - secondScore >= 3)
            {
                var parameterAnalysis = _parameterResolver.Analyze(question, best.Tool, matchedTerms: best.MatchedTerms);
                return new CopilotToolResolution
                {
                    IsMatch = true,
                    ToolName = best.Tool.ToolKey,
                    SearchText = parameterAnalysis.SearchText,
                    Parameters = parameterAnalysis.Parameters,
                    MissingParameters = parameterAnalysis.MissingParameters,
                    Confidence = RoutingConfidence.High,
                    RequiresClarification = parameterAnalysis.RequiresClarification,
                    ClarificationQuestion = parameterAnalysis.RequiresClarification
                        ? _parameterResolver.BuildClarificationQuestion(best.Tool, parameterAnalysis.MissingParameters)
                        : "",
                    Reason = CopilotTextTemplate.Apply(
                        _text.ToolResolverDeterministicReasonTemplate,
                        new Dictionary<string, string?> { ["TOOL_TITLE"] = best.Tool.Title })
                };
            }

            return await ResolveByModelAsync(question, candidates.Take(4).Select(candidate => candidate.Tool).ToList(), cancellationToken);
        }

        private async Task<CopilotToolResolution> ResolveByModelAsync(
            string question,
            IReadOnlyCollection<CopilotToolDefinition> candidates,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var provider = _providerFactory.GetActiveProvider();
                var prompt = BuildPrompt(question, candidates);
                var result = await provider.GenerateAsync(prompt);
                if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
                {
                    return new CopilotToolResolution
                    {
                        Reason = _text.ToolResolverModelNoResponseReason
                    };
                }

                var parsed = ParseResolution(result.ResponseText);
                if (parsed != null)
                {
                    if (!parsed.IsMatch || string.IsNullOrWhiteSpace(parsed.ToolName) || string.Equals(parsed.ToolName, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        return parsed;
                    }

                    var selectedTool = candidates.FirstOrDefault(tool => string.Equals(tool.ToolKey, parsed.ToolName, StringComparison.OrdinalIgnoreCase))
                        ?? await _toolRegistry.GetByKeyAsync(parsed.ToolName);
                    if (selectedTool == null)
                    {
                        return parsed;
                    }

                    var parameterAnalysis = _parameterResolver.Analyze(
                        question,
                        selectedTool,
                        parsed.Parameters,
                        preferredSearchText: parsed.SearchText);
                    parsed.SearchText = parameterAnalysis.SearchText;
                    parsed.Parameters = parameterAnalysis.Parameters;
                    parsed.MissingParameters = parameterAnalysis.MissingParameters;
                    parsed.RequiresClarification = parameterAnalysis.RequiresClarification;
                    parsed.ClarificationQuestion = parameterAnalysis.RequiresClarification
                        ? _parameterResolver.BuildClarificationQuestion(selectedTool, parameterAnalysis.MissingParameters)
                        : "";
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Model-assisted tool routing failed.");
            }

            return new CopilotToolResolution
            {
                Reason = _text.ToolResolverAmbiguousReason
            };
        }

        private static List<ToolMatchCandidate> ScoreCandidates(string question, IEnumerable<CopilotToolDefinition> tools, HashSet<string> stopWords)
        {
            var normalizedQuestion = Normalize(question);
            var questionTokens = Tokenize(normalizedQuestion, stopWords);
            var candidates = new List<ToolMatchCandidate>();

            foreach (var tool in tools)
            {
                var candidate = new ToolMatchCandidate { Tool = tool };
                var normalizedTitle = Normalize(tool.Title);
                var normalizedTestPrompt = Normalize(tool.TestPrompt ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(normalizedTitle) &&
                    normalizedQuestion.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase))
                {
                    candidate.Score += 8;
                    candidate.MatchedTerms.Add(normalizedTitle);
                }

                if (!string.IsNullOrWhiteSpace(normalizedTestPrompt))
                {
                    if (string.Equals(normalizedQuestion, normalizedTestPrompt, StringComparison.OrdinalIgnoreCase))
                    {
                        candidate.Score += 10;
                        candidate.MatchedTerms.Add("test-prompt-exact");
                    }
                    else
                    {
                        candidate.Score += CountOverlap(questionTokens, Tokenize(tool.TestPrompt, stopWords), maxWeight: 4);
                    }
                }

                foreach (var keyword in SplitTerms(tool.KeywordHints))
                {
                    if (normalizedQuestion.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        candidate.Score += keyword.Contains(' ') ? 7 : 5;
                        candidate.MatchedTerms.Add(keyword);
                    }
                }

                foreach (var token in Tokenize(tool.Title, stopWords))
                {
                    if (questionTokens.Contains(token))
                    {
                        candidate.Score += 3;
                        candidate.MatchedTerms.Add(token);
                    }
                }

                candidate.Score += CountOverlap(questionTokens, Tokenize(tool.Description, stopWords), maxWeight: 3);
                candidate.Score += CountOverlap(questionTokens, Tokenize(tool.QueryExtractionHint, stopWords), maxWeight: 2);

                candidates.Add(candidate);
            }

            return candidates;
        }

        private static int CountOverlap(HashSet<string> questionTokens, IEnumerable<string> candidateTokens, int maxWeight)
        {
            var matches = candidateTokens.Count(questionTokens.Contains);
            return Math.Min(matches, maxWeight);
        }

        private static IReadOnlyCollection<string> SplitTerms(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Normalize)
                    .Where(term => !string.IsNullOrWhiteSpace(term))
                    .ToArray();
        }

        private static HashSet<string> Tokenize(string? value, HashSet<string> stopWords)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return [];
            }

            return Regex.Matches(Normalize(value), @"[\p{L}\p{N}]{3,}")
                .Select(match => match.Value)
                .Where(token => !stopWords.Contains(token))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private string BuildPrompt(string question, IReadOnlyCollection<CopilotToolDefinition> tools)
        {
            var catalog = tools.Select(tool => new
            {
                tool.ToolKey,
                tool.Title,
                tool.Description,
                EndpointParameters = CopilotToolParameterResolverService.Describe(tool.EndpointUrl),
                tool.KeywordHints,
                tool.QueryExtractionHint,
                tool.TestPrompt
            });
            return CopilotTextTemplate.Apply(
                CopilotTextTemplate.JoinLines(_text.ToolResolverPromptLines),
                new Dictionary<string, string?>
                {
                    ["QUESTION"] = question,
                    ["TOOL_CATALOG"] = JsonSerializer.Serialize(catalog)
                });
        }

        private static CopilotToolResolution? ParseResolution(string responseText)
        {
            try
            {
                var json = CopilotJsonHelper.ExtractJson(responseText);
                using var doc = JsonDocument.Parse(json);
                var decision = doc.RootElement.TryGetProperty("decision", out var decisionProperty)
                    ? decisionProperty.GetString() ?? "no_match"
                    : "no_match";

                var isMatch = string.Equals(decision, "match", StringComparison.OrdinalIgnoreCase);
                return new CopilotToolResolution
                {
                    IsMatch = isMatch,
                    ToolName = isMatch ? CopilotJsonHelper.GetString(doc, "toolName", "none") : "none",
                    SearchText = isMatch ? CopilotJsonHelper.GetString(doc, "searchText") : "",
                    Parameters = isMatch ? CopilotJsonHelper.GetDictionary(doc, "toolParameters") : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Confidence = CopilotJsonHelper.GetEnum(doc, "confidence", RoutingConfidence.Medium),
                    Reason = CopilotJsonHelper.GetString(doc, "reason")
                };
            }
            catch
            {
                return null;
            }
        }

        private static string Normalize(string value)
            => string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        private sealed class ToolMatchCandidate
        {
            public CopilotToolDefinition Tool { get; set; } = new();
            public int Score { get; set; }
            public HashSet<string> MatchedTerms { get; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Tool parameter extraction stays next to tool intent routing because both belong to the same external-tool step.
    /// </summary>
    public class CopilotToolParameterResolverService
    {
        private static readonly Regex EndpointParameterRegex = new(@"\{([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);
        private static readonly Regex NumberRegex = new(@"\b\d+\b", RegexOptions.Compiled);
        private static readonly Regex TicketNumberRegex = new(@"\b[a-z]{2,}-?\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IsoDateRegex = new(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);
        private readonly CopilotHeuristicCatalog _heuristics;

        public CopilotToolParameterResolverService(CopilotHeuristicCatalog heuristics)
        {
            _heuristics = heuristics;
        }

        public CopilotToolParameterAnalysis Analyze(
            string question,
            CopilotToolDefinition tool,
            IReadOnlyDictionary<string, string>? seedParameters = null,
            IReadOnlyCollection<string>? matchedTerms = null,
            string? preferredSearchText = null)
        {
            var requirements = Describe(tool.EndpointUrl);
            var analysis = new CopilotToolParameterAnalysis
            {
                SearchText = ExtractSearchText(question, tool, matchedTerms, preferredSearchText),
                Requirements = requirements
            };

            if (!requirements.Any())
            {
                return analysis;
            }

            foreach (var seeded in seedParameters ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrWhiteSpace(seeded.Value))
                {
                    analysis.Parameters[seeded.Key] = seeded.Value.Trim();
                }
            }

            foreach (var requirement in requirements)
            {
                if (analysis.Parameters.ContainsKey(requirement.Name))
                {
                    continue;
                }

                var value = ResolveValue(question, analysis.SearchText, requirement, requirements.Count);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    analysis.Parameters[requirement.Name] = value;
                }
            }

            if (requirements.Count == 1 &&
                !string.IsNullOrWhiteSpace(analysis.SearchText) &&
                !analysis.Parameters.ContainsKey(requirements[0].Name))
            {
                analysis.Parameters[requirements[0].Name] = analysis.SearchText;
            }

            foreach (var requirement in requirements.Where(requirement =>
                         requirement.IsRequired &&
                         !analysis.Parameters.ContainsKey(requirement.Name)))
            {
                analysis.MissingParameters.Add(requirement.Name);
            }

            if (string.IsNullOrWhiteSpace(analysis.SearchText))
            {
                var queryRequirement = requirements.FirstOrDefault(requirement =>
                    string.Equals(requirement.Name, "query", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(requirement.Name, "search", StringComparison.OrdinalIgnoreCase));
                if (queryRequirement != null && analysis.Parameters.TryGetValue(queryRequirement.Name, out var resolvedQuery))
                {
                    analysis.SearchText = resolvedQuery;
                }
            }

            return analysis;
        }

        public string BuildClarificationQuestion(CopilotToolDefinition tool, IEnumerable<string> missingParameters)
        {
            var labels = missingParameters
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(ToDisplayLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!labels.Any())
            {
                return $"I can use the external tool '{tool.Title}', but I still need more details.";
            }

            return $"I can use the external tool '{tool.Title}', but I still need: {string.Join(", ", labels)}.";
        }

        public static List<CopilotToolParameterRequirement> Describe(string? endpointUrl)
        {
            if (string.IsNullOrWhiteSpace(endpointUrl))
            {
                return new List<CopilotToolParameterRequirement>();
            }

            return EndpointParameterRegex.Matches(endpointUrl)
                .Select(match => match.Groups[1].Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new CopilotToolParameterRequirement
                {
                    Name = name,
                    Type = InferType(name),
                    Aliases = BuildAliases(name)
                })
                .ToList();
        }

        private string ExtractSearchText(
            string question,
            CopilotToolDefinition tool,
            IReadOnlyCollection<string>? matchedTerms,
            string? preferredSearchText)
        {
            if (!string.IsNullOrWhiteSpace(preferredSearchText))
            {
                return preferredSearchText.Trim();
            }

            var trimmedQuestion = question.Trim();
            var lowerQuestion = trimmedQuestion.ToLowerInvariant();

            var hintedValue = TryExtractHintedSearchText(trimmedQuestion, tool);
            if (!string.IsNullOrWhiteSpace(hintedValue))
            {
                return hintedValue;
            }

            foreach (var term in (matchedTerms ?? Array.Empty<string>()).OrderByDescending(term => term.Length))
            {
                var index = lowerQuestion.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                var tail = trimmedQuestion[(index + term.Length)..].Trim(' ', '?', '.', '!', ',', ';', ':');
                tail = TrimLeadingTerm(tail);
                if (!string.IsNullOrWhiteSpace(tail) && tail.Length < trimmedQuestion.Length)
                {
                    return tail;
                }
            }

            return trimmedQuestion;
        }

        private static string? TryExtractHintedSearchText(string question, CopilotToolDefinition tool)
        {
            var hint = tool.QueryExtractionHint?.ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(hint))
            {
                return null;
            }

            if (hint.Contains("currency code", StringComparison.OrdinalIgnoreCase))
            {
                var currencyMatch = Regex.Match(question, @"\b[A-Z]{3}\b");
                if (currencyMatch.Success)
                {
                    return currencyMatch.Value;
                }

                var currencyTail = Regex.Match(question, @"\b(?:currency|code)\s+(?<value>[A-Za-z]{3})\b", RegexOptions.IgnoreCase);
                if (currencyTail.Success)
                {
                    return currencyTail.Groups["value"].Value.ToUpperInvariant();
                }
            }

            if (hint.Contains("country name", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractTail(question, @"\b(?:about|for|in)\s+(?<value>[^,;\.\?\n]+)");
            }

            if (hint.Contains("place or city name", StringComparison.OrdinalIgnoreCase) ||
                hint.Contains("place or city", StringComparison.OrdinalIgnoreCase) ||
                hint.Contains("city name", StringComparison.OrdinalIgnoreCase) ||
                hint.Contains("place name", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractTail(question, @"\b(?:for|in|at|near)\s+(?<value>[^,;\.\?\n]+)");
            }

            if (hint.Contains("country name only", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractTail(question, @"\b(?:about|for|in)\s+(?<value>[^,;\.\?\n]+)");
            }

            return null;
        }

        private static string? ResolveValue(string question, string searchText, CopilotToolParameterRequirement requirement, int parameterCount)
        {
            if (IsQueryParameter(requirement.Name))
            {
                return searchText;
            }

            if (string.Equals(requirement.Type, "number", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveNumber(question, requirement.Name);
            }

            if (string.Equals(requirement.Type, "date", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveDate(question, requirement.Name);
            }

            if (string.Equals(requirement.Type, "boolean", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveBoolean(question);
            }

            if (requirement.Aliases.Any(alias => alias.Contains("ticket", StringComparison.OrdinalIgnoreCase)))
            {
                var ticket = TicketNumberRegex.Match(question);
                if (ticket.Success)
                {
                    return ticket.Value;
                }
            }

            var namedValue = ResolveNamedTextValue(question, requirement);
            if (!string.IsNullOrWhiteSpace(namedValue))
            {
                return namedValue;
            }

            return parameterCount == 1 ? searchText : null;
        }

        private static string? ResolveNumber(string question, string parameterName)
        {
            var matches = NumberRegex.Matches(question);
            if (matches.Count == 0)
            {
                return null;
            }

            if (parameterName.Contains("page", StringComparison.OrdinalIgnoreCase) && matches.Count > 1)
            {
                return matches[1].Value;
            }

            return matches[0].Value;
        }

        private static string? ResolveDate(string question, string parameterName)
        {
            var matches = IsoDateRegex.Matches(question).Select(match => match.Value).ToList();
            if (!matches.Any())
            {
                return null;
            }

            if (parameterName.Contains("end", StringComparison.OrdinalIgnoreCase) || parameterName.Contains("to", StringComparison.OrdinalIgnoreCase))
            {
                return matches.Last();
            }

            return matches.First();
        }

        private static string? ResolveBoolean(string question)
        {
            if (Regex.IsMatch(question, @"\b(true|yes|enabled|active)\b", RegexOptions.IgnoreCase))
            {
                return "true";
            }

            if (Regex.IsMatch(question, @"\b(false|no|disabled|inactive)\b", RegexOptions.IgnoreCase))
            {
                return "false";
            }

            return null;
        }

        private static string? ResolveNamedTextValue(string question, CopilotToolParameterRequirement requirement)
        {
            foreach (var alias in requirement.Aliases.OrderByDescending(alias => alias.Length))
            {
                var pattern = $@"\b{Regex.Escape(alias)}\b\s*(?:=|:|is|named|called)?\s*(?<value>[^,;\.\?\n]+)";
                var match = Regex.Match(question, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return SanitizeValue(match.Groups["value"].Value);
                }
            }

            if (requirement.Aliases.Any(alias => alias is "city" or "location" or "country" or "place"))
            {
                var locationMatch = Regex.Match(question, @"\b(?:in|at|for)\s+(?<value>[^,;\.\?\n]+)", RegexOptions.IgnoreCase);
                if (locationMatch.Success)
                {
                    return SanitizeValue(locationMatch.Groups["value"].Value);
                }
            }

            return null;
        }

        private static string InferType(string parameterName)
        {
            if (parameterName.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                parameterName.Contains("time", StringComparison.OrdinalIgnoreCase))
            {
                return "date";
            }

            if ((parameterName.StartsWith("is", StringComparison.OrdinalIgnoreCase) ||
                 parameterName.StartsWith("has", StringComparison.OrdinalIgnoreCase)) &&
                !parameterName.Contains("history", StringComparison.OrdinalIgnoreCase))
            {
                return "boolean";
            }

            if ((parameterName.Contains("id", StringComparison.OrdinalIgnoreCase) &&
                 !parameterName.Contains("ticket", StringComparison.OrdinalIgnoreCase)) ||
                parameterName.Contains("count", StringComparison.OrdinalIgnoreCase) ||
                parameterName.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
                parameterName.Contains("size", StringComparison.OrdinalIgnoreCase) ||
                parameterName.Contains("page", StringComparison.OrdinalIgnoreCase))
            {
                return "number";
            }

            return "text";
        }

        private static List<string> BuildAliases(string parameterName)
        {
            var parts = Regex.Matches(parameterName, @"[A-Z]?[a-z]+|[0-9]+")
                .Select(match => match.Value.ToLowerInvariant())
                .ToList();

            if (!parts.Any())
            {
                parts = parameterName
                    .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(part => part.ToLowerInvariant())
                    .ToList();
            }

            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                parameterName,
                parameterName.Replace("_", " ", StringComparison.OrdinalIgnoreCase).ToLowerInvariant(),
                string.Join(" ", parts)
            };

            foreach (var part in parts)
            {
                aliases.Add(part);
            }

            return aliases
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .ToList();
        }

        private string TrimLeadingTerm(string value)
        {
            var lowered = value.ToLowerInvariant();

            foreach (var prefix in _heuristics.ToolLeadingTerms)
            {
                var normalizedPrefix = prefix.EndsWith(' ') ? prefix : $"{prefix} ";
                if (lowered.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return value[normalizedPrefix.Length..].Trim();
                }
            }

            return value;
        }

        private static bool IsQueryParameter(string parameterName)
            => parameterName is "q" or "query" or "search" or "searchText" or "term" or "text";

        private static string SanitizeValue(string value)
        {
            var sanitized = value.Trim();
            sanitized = Regex.Replace(sanitized, @"\s+(?:and|then|also|plus|with)\b.*$", "", RegexOptions.IgnoreCase);
            return sanitized.Trim(' ', ',', ';', '.', '?');
        }

        private static string? ExtractTail(string question, string pattern)
        {
            var match = Regex.Match(question, pattern, RegexOptions.IgnoreCase);
            return match.Success ? SanitizeValue(match.Groups["value"].Value) : null;
        }

        private static string ToDisplayLabel(string parameterName)
        {
            var label = Regex.Replace(parameterName, "([a-z])([A-Z])", "$1 $2");
            label = label.Replace("_", " ", StringComparison.OrdinalIgnoreCase);
            return label.ToLowerInvariant();
        }
    }
}
