using System.Text;
using System.Text.Json;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using AISupportAnalysisPlatform.Services.AI.Providers;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotExternalToolExecutor
    {
        private readonly CopilotToolRegistryService _toolRegistry;
        private readonly ICopilotToolIntentResolver _toolIntentResolver;
        private readonly CopilotToolParameterResolverService _toolParameterResolver;
        private readonly IAiProviderFactory _providerFactory;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly CopilotTextCatalog _text;
        private readonly ILogger<CopilotExternalToolExecutor> _logger;

        public CopilotExternalToolExecutor(
            CopilotToolRegistryService toolRegistry,
            ICopilotToolIntentResolver toolIntentResolver,
            CopilotToolParameterResolverService toolParameterResolver,
            IAiProviderFactory providerFactory,
            IHttpClientFactory httpClientFactory,
            CopilotTextCatalog text,
            ILogger<CopilotExternalToolExecutor> logger)
        {
            _toolRegistry = toolRegistry;
            _toolIntentResolver = toolIntentResolver;
            _toolParameterResolver = toolParameterResolver;
            _providerFactory = providerFactory;
            _httpClientFactory = httpClientFactory;
            _text = text;
            _logger = logger;
        }

        public async Task<CopilotExecutionResult> ExecuteAsync(
            CopilotChatRequest request,
            CopilotExecutionPlan plan,
            CancellationToken cancellationToken = default)
        {
            var executionSteps = new List<CopilotExecutionStep>();
            var toolKey = plan.ToolName;
            var query = string.IsNullOrWhiteSpace(plan.SearchText) ? request.Question : plan.SearchText;
            var parameters = new Dictionary<string, string>(plan.ToolParameters, StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(toolKey) || toolKey == "none")
            {
                var resolution = await _toolIntentResolver.ResolveAsync(request.Question, cancellationToken);
                toolKey = resolution.IsMatch ? resolution.ToolName : "none";
                if (!string.IsNullOrWhiteSpace(resolution.SearchText))
                {
                    query = resolution.SearchText;
                }
                parameters = resolution.Parameters;

                executionSteps.Add(new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.Executor,
                    Action = "Dynamic Tool Resolution",
                    Detail = $"Input: \"{request.Question}\"\nOutput: Tool Key: {toolKey}",
                    Status = toolKey != "none" ? CopilotStepStatus.Ok : CopilotStepStatus.Warn
                });
            }

            var tool = toolKey == "none" ? null : await _toolRegistry.GetByKeyAsync(toolKey);
            if (tool == null || !tool.IsEnabled || string.IsNullOrWhiteSpace(tool.EndpointUrl))
            {
                return new CopilotExecutionResult
                {
                    Answer = _text.ExternalToolMissingAnswer,
                    Notes = _text.ExternalToolMissingNotes,
                    UsedTool = "none",
                    ResponseMode = ResponseMode.Conversational,
                    EvidenceStrength = EvidenceStrength.Weak,
                    Summary = _text.ExternalToolLookupFailedSummary,
                    ExecutionSteps = executionSteps
                };
            }

            var parameterAnalysis = _toolParameterResolver.Analyze(
                request.Question,
                tool,
                parameters,
                preferredSearchText: query);

            executionSteps.Add(new CopilotExecutionStep
            {
                Layer = CopilotExecutionLayer.Executor,
                Action = "Parameter Extraction",
                Detail = $"Input: {tool.Title} schema\nOutput: {parameterAnalysis.Parameters.Count} parameters resolved.",
                Status = parameterAnalysis.RequiresClarification ? CopilotStepStatus.Warn : CopilotStepStatus.Ok
            });

            if (parameterAnalysis.RequiresClarification)
            {
                return new CopilotExecutionResult
                {
                    Answer = _toolParameterResolver.BuildClarificationQuestion(tool, parameterAnalysis.MissingParameters),
                    Notes = _text.ClarificationRequiredNotes,
                    UsedTool = tool.ToolKey,
                    ResponseMode = ResponseMode.Conversational,
                    EvidenceStrength = EvidenceStrength.Weak,
                    Summary = _text.ExternalToolClarificationSummary,
                    ExecutionSteps = executionSteps
                };
            }

            query = string.IsNullOrWhiteSpace(parameterAnalysis.SearchText) ? query : parameterAnalysis.SearchText;
            parameters = parameterAnalysis.Parameters;

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                var url = BuildToolUrl(tool.EndpointUrl, query, parameters);
                
                var response = await client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                var trimmedPayload = payload.Length > 3000 ? payload[..3000] : payload;

                executionSteps.Add(new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.Executor,
                    Action = "External API Dispatch",
                    Detail = $"Input: {url}\nOutput: Received {payload.Length} bytes.",
                    TechnicalData = trimmedPayload
                });

                if (TryBuildDeterministicAnswer(tool, query, payload, out var deterministicAnswer))
                {
                    executionSteps.Add(new CopilotExecutionStep
                    {
                        Layer = CopilotExecutionLayer.Executor,
                        Action = "Format tool result locally",
                        Detail = "Converted the tool payload into a direct answer without waiting for an extra model-summary pass.",
                        Status = CopilotStepStatus.Ok
                    });

                    return new CopilotExecutionResult
                    {
                        Answer = deterministicAnswer,
                        UsedTool = tool.ToolKey,
                        TechnicalData = trimmedPayload,
                        ResponseMode = ResponseMode.MetricSummary,
                        EvidenceStrength = EvidenceStrength.High,
                        Summary = CopilotTextTemplate.Apply(
                            _text.ExternalToolSuccessSummaryTemplate,
                            new Dictionary<string, string?> { ["TOOL_TITLE"] = tool.Title }),
                        ExecutionSteps = executionSteps,
                        IsDeterministicEvidenceAnswer = true
                    };
                }

                var provider = _providerFactory.GetActiveProvider();
                var summaryPrompt = CopilotTextTemplate.Apply(
                    CopilotTextTemplate.JoinLines(_text.ExternalToolSummaryPromptLines),
                    new Dictionary<string, string?>
                    {
                        ["TOOL_TITLE"] = tool.Title,
                        ["QUESTION"] = request.Question,
                        ["PAYLOAD"] = trimmedPayload
                    });
                var llm = await provider.GenerateAsync(summaryPrompt);

                executionSteps.Add(new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.Executor,
                    Action = "Tool Result Summarization",
                    Detail = "The payload shape was not simple enough for direct formatting, so the model generated a concise summary.",
                    Status = llm.Success ? CopilotStepStatus.Ok : CopilotStepStatus.Warn
                });

                return new CopilotExecutionResult
                {
                    Answer = llm.Success && !string.IsNullOrWhiteSpace(llm.ResponseText)
                        ? llm.ResponseText.Trim()
                        : CopilotTextTemplate.Apply(
                            _text.ExternalToolSummaryFallbackTemplate,
                            new Dictionary<string, string?> { ["TOOL_TITLE"] = tool.Title }),
                    UsedTool = tool.ToolKey,
                    TechnicalData = trimmedPayload,
                    ResponseMode = ResponseMode.MetricSummary,
                    EvidenceStrength = EvidenceStrength.High,
                    Summary = CopilotTextTemplate.Apply(
                        _text.ExternalToolSuccessSummaryTemplate,
                        new Dictionary<string, string?> { ["TOOL_TITLE"] = tool.Title }),
                    ExecutionSteps = executionSteps
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "External tool execution failed for {ToolKey}.", tool.ToolKey);
                executionSteps.Add(new CopilotExecutionStep
                {
                    Layer = CopilotExecutionLayer.Executor,
                    Action = "Tool Execution Error",
                    Detail = ex.Message,
                    Status = CopilotStepStatus.Error
                });

                return new CopilotExecutionResult
                {
                    Answer = CopilotTextTemplate.Apply(
                        _text.ExternalToolFailureAnswerTemplate,
                        new Dictionary<string, string?>
                        {
                            ["TOOL_TITLE"] = tool.Title,
                            ["ERROR"] = ex.Message
                        }),
                    UsedTool = tool.ToolKey,
                    ResponseMode = ResponseMode.Conversational,
                    EvidenceStrength = EvidenceStrength.Weak,
                    Summary = _text.ExternalToolFailureSummary,
                    ExecutionSteps = executionSteps
                };
            }
        }

        private static string BuildToolUrl(string endpointUrl, string query, IReadOnlyDictionary<string, string> parameters)
        {
            var resolvedUrl = endpointUrl;
            var resolvedParameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
            if (!resolvedParameters.ContainsKey("query"))
            {
                resolvedParameters["query"] = query;
            }

            foreach (var parameter in resolvedParameters)
            {
                resolvedUrl = resolvedUrl.Replace($"{{{parameter.Key}}}", Uri.EscapeDataString(parameter.Value ?? string.Empty), StringComparison.OrdinalIgnoreCase);
            }

            return resolvedUrl;
        }

        private static bool TryBuildDeterministicAnswer(
            CopilotToolDefinition tool,
            string query,
            string payload,
            out string answer)
        {
            answer = string.Empty;

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;

                if (TryBuildCountryProfileAnswer(root, out answer))
                {
                    return true;
                }

                if (TryBuildLocationAnswer(root, out answer))
                {
                    return true;
                }

                if (TryBuildFxAnswer(root, out answer))
                {
                    return true;
                }

                if (TryBuildHolidayAnswer(root, out answer))
                {
                    return true;
                }

                if (TryBuildUniversityAnswer(root, out answer))
                {
                    return true;
                }

                if (TryBuildCountryListAnswer(root, query, out answer))
                {
                    return true;
                }

                if (TryBuildGenericJsonAnswer(tool, root, out answer))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Non-JSON payloads can fall through to the model summary path.
            }

            return false;
        }

        private static bool TryBuildCountryProfileAnswer(JsonElement root, out string answer)
        {
            answer = string.Empty;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return false;
            }

            var country = root[0];
            if (!TryGetProperty(country, "capital", out _) ||
                !TryGetProperty(country, "currencies", out _) ||
                !TryGetProperty(country, "timezones", out _))
            {
                return false;
            }

            var name = TryGetNestedString(country, "name", "common") ?? "The requested country";
            var capital = TryGetFirstArrayValue(country, "capital") ?? "Unknown";
            var region = country.TryGetProperty("region", out var regionElement) ? regionElement.GetString() ?? "Unknown" : "Unknown";
            var populationText = country.TryGetProperty("population", out var populationElement) && populationElement.TryGetInt64(out var populationValue)
                ? populationValue.ToString("N0")
                : "Unknown";
            var currencies = TryGetObjectPropertyNames(country, "currencies");
            var timezones = TryGetStringArray(country, "timezones");

            var builder = new StringBuilder();
            builder.AppendLine($"**{name}**");
            builder.AppendLine($"- Capital: {capital}");
            builder.AppendLine($"- Region: {region}");
            builder.AppendLine($"- Population: {populationText}");
            if (currencies.Count > 0)
            {
                builder.AppendLine($"- Currency: {string.Join(", ", currencies)}");
            }
            if (timezones.Count > 0)
            {
                builder.AppendLine($"- Time zones: {string.Join(", ", timezones.Take(3))}");
            }

            answer = builder.ToString().TrimEnd();
            return true;
        }

        private static bool TryBuildCountryListAnswer(JsonElement root, string query, out string answer)
        {
            answer = string.Empty;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return false;
            }

            var first = root[0];
            if (!TryGetProperty(first, "name", out _) || !TryGetProperty(first, "currencies", out _))
            {
                return false;
            }

            var countries = root.EnumerateArray()
                .Select(country => TryGetNestedString(country, "name", "common"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(8)
                .ToList();

            if (countries.Count == 0)
            {
                return false;
            }

            answer = $"Countries matching **{query}**: {string.Join(", ", countries)}.";
            return true;
        }

        private static bool TryBuildLocationAnswer(JsonElement root, out string answer)
        {
            answer = string.Empty;
            if (!TryGetProperty(root, "results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array || resultsElement.GetArrayLength() == 0)
            {
                return false;
            }

            var builder = new StringBuilder();
            builder.AppendLine("Best matching locations:");

            foreach (var location in resultsElement.EnumerateArray().Take(5))
            {
                var name = location.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "Unknown" : "Unknown";
                var country = location.TryGetProperty("country", out var countryElement) ? countryElement.GetString() ?? "Unknown" : "Unknown";
                var timezone = location.TryGetProperty("timezone", out var timezoneElement) ? timezoneElement.GetString() ?? "Unknown" : "Unknown";
                var latitude = location.TryGetProperty("latitude", out var latitudeElement) ? latitudeElement.ToString() : "-";
                var longitude = location.TryGetProperty("longitude", out var longitudeElement) ? longitudeElement.ToString() : "-";

                builder.AppendLine($"- **{name}, {country}**: {latitude}, {longitude} ({timezone})");
            }

            answer = builder.ToString().TrimEnd();
            return true;
        }

        private static bool TryBuildFxAnswer(JsonElement root, out string answer)
        {
            answer = string.Empty;
            if (!TryGetProperty(root, "rates", out var ratesElement) || ratesElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var baseCurrency = root.TryGetProperty("base", out var baseElement) ? baseElement.GetString() ?? "Base" : "Base";
            var date = root.TryGetProperty("date", out var dateElement) ? dateElement.GetString() ?? string.Empty : string.Empty;
            var rates = ratesElement.EnumerateObject()
                .Select(rate => $"{rate.Name}: {rate.Value}")
                .ToList();

            if (rates.Count == 0)
            {
                return false;
            }

            answer = $"{baseCurrency} exchange snapshot{(string.IsNullOrWhiteSpace(date) ? string.Empty : $" ({date})")}: {string.Join(", ", rates)}.";
            return true;
        }

        private static bool TryBuildHolidayAnswer(JsonElement root, out string answer)
        {
            answer = string.Empty;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return false;
            }

            var first = root[0];
            if (!TryGetProperty(first, "date", out _) || !TryGetProperty(first, "countryCode", out _))
            {
                return false;
            }

            var builder = new StringBuilder();
            builder.AppendLine("Upcoming public holidays:");
            foreach (var holiday in root.EnumerateArray().Take(6))
            {
                var name = holiday.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "Holiday" : "Holiday";
                var date = holiday.TryGetProperty("date", out var dateElement) ? dateElement.GetString() ?? "-" : "-";
                var countryCode = holiday.TryGetProperty("countryCode", out var countryCodeElement) ? countryCodeElement.GetString() ?? "-" : "-";
                builder.AppendLine($"- **{date}**: {name} ({countryCode})");
            }

            answer = builder.ToString().TrimEnd();
            return true;
        }

        private static bool TryBuildUniversityAnswer(JsonElement root, out string answer)
        {
            answer = string.Empty;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return false;
            }

            var first = root[0];
            if (!TryGetProperty(first, "web_pages", out _))
            {
                return false;
            }

            var builder = new StringBuilder();
            builder.AppendLine("Matching universities:");
            foreach (var university in root.EnumerateArray().Take(8))
            {
                var name = university.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "Unknown" : "Unknown";
                var websites = TryGetStringArray(university, "web_pages");
                var website = websites.FirstOrDefault() ?? "-";
                builder.AppendLine($"- **{name}**: {website}");
            }

            answer = builder.ToString().TrimEnd();
            return true;
        }

        private static bool TryBuildGenericJsonAnswer(CopilotToolDefinition tool, JsonElement root, out string answer)
        {
            answer = string.Empty;

            if (root.ValueKind == JsonValueKind.Object)
            {
                var properties = root.EnumerateObject()
                    .Take(8)
                    .Select(property => $"{Humanize(property.Name)}: {FormatScalarValue(property.Value)}")
                    .ToList();

                if (properties.Count == 0)
                {
                    return false;
                }

                answer = $"**{tool.Title}**\n- {string.Join("\n- ", properties)}";
                return true;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                var items = root.EnumerateArray()
                    .Take(5)
                    .Select(FormatScalarValue)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();

                if (items.Count == 0)
                {
                    return false;
                }

                answer = $"**{tool.Title}**\n- {string.Join("\n- ", items)}";
                return true;
            }

            return false;
        }

        private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            value = default;
            return element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(propertyName, out value);
        }

        private static string? TryGetNestedString(JsonElement element, string propertyName, string nestedProperty)
        {
            if (!TryGetProperty(element, propertyName, out var outer) || outer.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return outer.TryGetProperty(nestedProperty, out var nested)
                ? nested.GetString()
                : null;
        }

        private static string? TryGetFirstArrayValue(JsonElement element, string propertyName)
        {
            if (!TryGetProperty(element, propertyName, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
            {
                return null;
            }

            var first = array[0];
            return first.ValueKind == JsonValueKind.String ? first.GetString() : first.ToString();
        }

        private static List<string> TryGetStringArray(JsonElement element, string propertyName)
        {
            if (!TryGetProperty(element, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return array.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
        }

        private static List<string> TryGetObjectPropertyNames(JsonElement element, string propertyName)
        {
            if (!TryGetProperty(element, propertyName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return obj.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }

        private static string FormatScalarValue(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number => element.ToString(),
                JsonValueKind.True => "Yes",
                JsonValueKind.False => "No",
                JsonValueKind.Object => string.Join(", ", element.EnumerateObject().Take(4).Select(property => $"{Humanize(property.Name)}={FormatScalarValue(property.Value)}")),
                JsonValueKind.Array => string.Join(", ", element.EnumerateArray().Take(4).Select(FormatScalarValue)),
                JsonValueKind.Null => "-",
                _ => element.ToString()
            };
        }

        private static string Humanize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var builder = new StringBuilder(value.Length + 8);
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (index > 0 && char.IsUpper(character) && !char.IsUpper(value[index - 1]))
                {
                    builder.Append(' ');
                }

                builder.Append(character == '_' ? ' ' : character);
            }

            return builder.ToString().Trim();
        }
    }
}
