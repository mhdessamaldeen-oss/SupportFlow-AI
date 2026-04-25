namespace AISupportAnalysisPlatform.Models.AI
{
    public class CopilotChatRequest
    {
        public int? LastTraceId { get; set; }
        public string Surface { get; set; } = "";
        public string ReportStartDate { get; set; } = "";
        public string ReportEndDate { get; set; } = "";
        public string Question { get; set; } = "";
        public List<CopilotChatMessage> History { get; set; } = new();
    }

    public class CopilotChatMessage
    {
        public ChatMessageRole Role { get; set; } = ChatMessageRole.User;
        public string Content { get; set; } = "";
    }

    public class CopilotChatResponse
    {
        public int? TraceId { get; set; }
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
        public EvidenceStrength EvidenceStrength { get; set; } = EvidenceStrength.Weak;
        public double GroundingScore { get; set; } = 1.0;
        public string GroundingNotes { get; set; } = "";
        public ResponseMode ResponseMode { get; set; } = ResponseMode.KnowledgeMatch;
        public string UsedTool { get; set; } = "none";
        public string ModelName { get; set; } = "";
        public string Notes { get; set; } = "";
        public List<string> SuggestedPrompts { get; set; } = new();
        public List<CopilotTicketCitation> SimilarTickets { get; set; } = new();
        public List<KnowledgeBaseChunkMatch> KnowledgeMatches { get; set; } = new();
        public AdminCopilotExecutionDetails ExecutionDetails { get; set; } = new();
        public AdminCopilotDynamicTicketQueryPlan? DynamicQueryPlan { get; set; }
        public List<CopilotStructuredSubResult> StructuredQueryResults { get; set; } = new();
        public List<AdminCopilotTicketQueryRow> DynamicTicketResults { get; set; } = new();
        public List<string> StructuredColumns { get; set; } = new();
        public List<AdminCopilotStructuredResultRow> StructuredRows { get; set; } = new();
        public AdminCopilotActionPlan? ActionPlan { get; set; }

        public void ApplyResult(CopilotExecutionResult execution)
        {
            Answer = execution.Answer;
            Notes = string.IsNullOrWhiteSpace(Notes) ? execution.Notes : Notes;
            ResponseMode = execution.ResponseMode;
            EvidenceStrength = execution.EvidenceStrength;
            GroundingScore = execution.GroundingScore;
            GroundingNotes = execution.GroundingNotes;
            UsedTool = execution.UsedTool;
            SuggestedPrompts = execution.SuggestedPrompts;
            SimilarTickets = execution.SimilarTickets;
            KnowledgeMatches = execution.KnowledgeMatches;
            DynamicQueryPlan = execution.DynamicQueryPlan;
            StructuredQueryResults = execution.StructuredQueryResults;
            ActionPlan = execution.ActionPlan;
            DynamicTicketResults = execution.DynamicTicketResults;
            StructuredColumns = execution.StructuredColumns;
            StructuredRows = execution.StructuredRows;

            ExecutionDetails.Summary = execution.Summary;
            ExecutionDetails.LastTechnicalData = execution.TechnicalData;
            ExecutionDetails.QueryPlan = execution.DynamicQueryPlan;
            ExecutionDetails.QueryPlans = execution.StructuredQueryResults.Select(result => result.Execution.Plan).ToList();
            ExecutionDetails.ActionPlan = execution.ActionPlan;
        }
    }

    public class CopilotChatViewModel
    {
        public List<CopilotEvaluationTicketItem> RecentTickets { get; set; } = new();
        public List<CopilotToolDefinition> AvailableTools { get; set; } = new();
        public List<CopilotCapabilityItem> ExternalCapabilities { get; set; } = new();
        public List<CopilotPromptGroup> StandardPromptGroups { get; set; } = new();
        public List<CopilotTraceHistory> RecentTraces { get; set; } = new();
        public int KnowledgeDocumentCount { get; set; }
    }

    public class CopilotCapabilityItem
    {
        public string ToolKey { get; set; } = string.Empty;
        public string ToolTitle { get; set; } = string.Empty;
        public string ToolDescription { get; set; } = string.Empty;
        public List<string> Prompts { get; set; } = new();
    }

    public class CopilotPromptGroup
    {
        public string Title { get; set; } = string.Empty;
        public List<string> Prompts { get; set; } = new();
    }
}
