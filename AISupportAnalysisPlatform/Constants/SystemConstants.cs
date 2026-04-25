namespace AISupportAnalysisPlatform.Constants
{
    public static class RoleNames
    {
        public const string Admin = "Admin";
        public const string SupportAgent = "SupportAgent";
        public const string EndUser = "EndUser";
        public const string Viewer = "Viewer";
        public const string Client = "Client";
    }

    public static class TicketStatusNames
    {
        public const string New = "New";
        public const string Open = "Open";
        public const string InProgress = "In Progress";
        public const string Pending = "Pending";
        public const string Resolved = "Resolved";
        public const string Closed = "Closed";
        public const string Rejected = "Rejected";
    }

    public static class TicketPriorityNames
    {
        public const string Low = "Low";
        public const string Medium = "Medium";
        public const string High = "High";
        public const string Critical = "Critical";
    }

    public static class TicketSourceNames
    {
        public const string WebPortal = "Web Portal";
        public const string Email = "Email";
        public const string Phone = "Phone";
        public const string InternalRequest = "Internal Request";
        public const string DefaultFallback = "Portals";
    }

    public static class ReferenceDataTypes
    {
        public const string Category = "category";
        public const string Priority = "priority";
        public const string Status = "status";
        public const string Source = "source";
    }

    public static class SettingKeys
    {
        public const string AiActiveProvider = "AiActiveProvider";
        public const string DockerModel = "DockerModel";
        public const string LegacyDockerModelKey = "OllamaModel";
        public const string DockerBaseUrl = "AiDockerBaseUrl";
        public const string DockerTimeoutSeconds = "AiDockerTimeoutSeconds";
        public const string DockerMaxPromptChars = "AiDockerMaxPromptChars";
        public const string DockerMaxPromptTokens = "AiDockerMaxPromptTokens";
        public const string DockerTemperature = "AiDockerTemperature";
        public const string DockerNumCtx = "AiDockerNumCtx";
        public const string DockerNumPredict = "AiDockerNumPredict";
        public const string DefaultTheme = "DefaultTheme";
        public const string OpenAiApiKey = "AiOpenAiApiKey";
        public const string OpenAiModel = "AiOpenAiModel";
        public const string OpenAiBaseUrl = "AiOpenAiBaseUrl";
        public const string OpenAiTimeoutSeconds = "AiOpenAiTimeoutSeconds";
        public const string OpenAiMaxPromptChars = "AiOpenAiMaxPromptChars";
        public const string OpenAiTemperature = "AiOpenAiTemperature";
        public const string OpenAiMaxTokens = "AiOpenAiMaxTokens";
        public const string OpenAiOrganizationId = "AiOpenAiOrganizationId";
        public const string OpenAiProjectId = "AiOpenAiProjectId";
        public const string CloudEndpoint = "AiCloudEndpoint";
        public const string CloudApiKey = "AiCloudApiKey";
        public const string CloudModel = "AiCloudModel";
        public const string CloudDeploymentName = "AiCloudDeploymentName";
        public const string CloudTimeoutSeconds = "AiCloudTimeoutSeconds";
        public const string CloudMaxPromptChars = "AiCloudMaxPromptChars";
        public const string CloudTemperature = "AiCloudTemperature";
        public const string CloudMaxTokens = "AiCloudMaxTokens";
        public const string CloudApiVersion = "AiCloudApiVersion";
        public const string CloudAuthHeaderName = "AiCloudAuthHeaderName";
        public const string CloudUseBearerToken = "AiCloudUseBearerToken";
        public const string LocalAiBaseUrl = "AiLocalAiBaseUrl";
        public const string LocalAiModel = "AiLocalAiModel";
        public const string LocalAiApiKey = "AiLocalAiApiKey";
        public const string LocalAiTimeoutSeconds = "AiLocalAiTimeoutSeconds";
        public const string LocalAiMaxPromptChars = "AiLocalAiMaxPromptChars";
        public const string LocalAiTemperature = "AiLocalAiTemperature";
        public const string GeminiApiKey = "AiGeminiApiKey";
        public const string GeminiModel = "AiGeminiModel";
        public const string GeminiTemperature = "AiGeminiTemperature";
        public const string GeminiMaxTokens = "AiGeminiMaxTokens";
    }

    public static class AiProviderNames
    {
        public const string DockerLocal = "DockerLocal";
        public const string LegacyDockerLocalAlias = "LocalOllama";
        public const string OpenAI = "OpenAI";
        public const string Cloud = "Cloud";
        public const string LocalAI = "LocalAI";
        public const string Gemini = "Gemini";
    }
}
