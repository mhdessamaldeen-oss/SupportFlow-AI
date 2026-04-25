using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.Infrastructure;
using Microsoft.Extensions.Options;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Localized, user-facing copilot messages.
    /// Prompt templates stay in <see cref="CopilotTextCatalog"/> so model instructions and runtime text are no longer mixed.
    /// </summary>
    public sealed class CopilotMessageCatalog
    {
        private readonly ILocalizationService _localizer;
        private readonly IOptionsMonitor<CopilotTextSettings> _monitor;

        public CopilotMessageCatalog(ILocalizationService localizer, IOptionsMonitor<CopilotTextSettings> monitor)
        {
            _localizer = localizer;
            _monitor = monitor;
        }

        private CopilotTextSettings Settings => _monitor.CurrentValue;

        public string EmptyQuestionMessage => Resolve("Copilot.EmptyQuestionMessage", Settings.General.EmptyQuestionMessage);
        public string ClarificationRequiredNotes => Resolve("Copilot.ClarificationRequiredNotes", Settings.General.ClarificationRequiredNotes);
        public string UnsupportedAnswer => Resolve("Copilot.UnsupportedAnswer", Settings.General.UnsupportedAnswer);
        public string UnsupportedSummary => Resolve("Copilot.UnsupportedSummary", Settings.General.UnsupportedSummary);
        public string SecurityBlockReason => Resolve("Copilot.SecurityBlockReason", Settings.General.SecurityBlockReason);
        public string SecurityBlockMessage => Resolve("Copilot.SecurityBlockMessage", Settings.General.SecurityBlockMessage);
        public IReadOnlyList<string> DefaultSuggestedPrompts => Settings.General.DefaultSuggestedPrompts;

        public string GreetingReason => Resolve("Copilot.GreetingReason", Settings.Routing.GreetingReason);
        public string FollowUpAnalyticsReason => Resolve("Copilot.FollowUpAnalyticsReason", Settings.Routing.FollowUpAnalyticsReason);
        public string FollowUpInvestigationReason => Resolve("Copilot.FollowUpInvestigationReason", Settings.Routing.FollowUpInvestigationReason);
        public string TicketKnowledgeReason => Resolve("Copilot.TicketKnowledgeReason", Settings.Routing.TicketKnowledgeReason);
        public string TicketReferenceReason => Resolve("Copilot.TicketReferenceReason", Settings.Routing.TicketReferenceReason);
        public string StructuredDataReason => Resolve("Copilot.StructuredDataReason", Settings.Routing.StructuredDataReason);
        public string RoutingKnowledgeBaseQueryReason => Resolve("Copilot.RoutingKnowledgeBaseQueryReason", Settings.Routing.KnowledgeBaseQueryReason);
        public string ClassificationFallbackReason => Resolve("Copilot.ClassificationFallbackReason", Settings.Routing.ClassificationFallbackReason);

        public string ExternalToolMissingAnswer => Resolve("Copilot.ExternalToolMissingAnswer", Settings.ExternalTools.MissingAnswer);
        public string ExternalToolMissingNotes => Resolve("Copilot.ExternalToolMissingNotes", Settings.ExternalTools.MissingNotes);
        public string ExternalToolLookupFailedSummary => Resolve("Copilot.ExternalToolLookupFailedSummary", Settings.ExternalTools.LookupFailedSummary);
        public string ExternalToolClarificationSummary => Resolve("Copilot.ExternalToolClarificationSummary", Settings.ExternalTools.ClarificationSummary);
        public string ExternalToolSummaryFallbackTemplate => Resolve("Copilot.ExternalToolSummaryFallbackTemplate", Settings.ExternalTools.SummaryFallbackTemplate);
        public string ExternalToolSuccessSummaryTemplate => Resolve("Copilot.ExternalToolSuccessSummaryTemplate", Settings.ExternalTools.SuccessSummaryTemplate);
        public string ExternalToolFailureAnswerTemplate => Resolve("Copilot.ExternalToolFailureAnswerTemplate", Settings.ExternalTools.FailureAnswerTemplate);
        public string ExternalToolFailureSummary => Resolve("Copilot.ExternalToolFailureSummary", Settings.ExternalTools.FailureSummary);
        public string ToolResolverEmptyQuestionReason => Resolve("Copilot.ToolResolverEmptyQuestionReason", Settings.ExternalTools.ToolResolverEmptyQuestionReason);
        public string ToolResolverNoToolsReason => Resolve("Copilot.ToolResolverNoToolsReason", Settings.ExternalTools.ToolResolverNoToolsReason);
        public string ToolResolverNoMatchReason => Resolve("Copilot.ToolResolverNoMatchReason", Settings.ExternalTools.ToolResolverNoMatchReason);
        public string ToolResolverDeterministicReasonTemplate => Resolve("Copilot.ToolResolverDeterministicReasonTemplate", Settings.ExternalTools.ToolResolverDeterministicReasonTemplate);
        public string ToolResolverModelNoResponseReason => Resolve("Copilot.ToolResolverModelNoResponseReason", Settings.ExternalTools.ToolResolverModelNoResponseReason);
        public string ToolResolverAmbiguousReason => Resolve("Copilot.ToolResolverAmbiguousReason", Settings.ExternalTools.ToolResolverAmbiguousReason);
        public string GeneralChatNoToolsText => Resolve("Copilot.GeneralChatNoToolsText", Settings.GeneralChat.NoToolsText);
        public string GeneralChatFallbackAnswer => Resolve("Copilot.GeneralChatFallbackAnswer", Settings.GeneralChat.FallbackAnswer);

        public string KnowledgeMatchExecutionSummaryTemplate => Resolve("Copilot.KnowledgeMatchExecutionSummaryTemplate", Settings.Investigation.KnowledgeMatchExecutionSummaryTemplate);
        public string InvestigationFallbackTicketTemplate => Resolve("Copilot.InvestigationFallbackTicketTemplate", Settings.Investigation.FallbackTicketTemplate);
        public string InvestigationFallbackKnowledgeTemplate => Resolve("Copilot.InvestigationFallbackKnowledgeTemplate", Settings.Investigation.FallbackKnowledgeTemplate);
        public string InvestigationFallbackSimilarTemplate => Resolve("Copilot.InvestigationFallbackSimilarTemplate", Settings.Investigation.FallbackSimilarTemplate);
        public string InvestigationFallbackNoEvidence => Resolve("Copilot.InvestigationFallbackNoEvidence", Settings.Investigation.FallbackNoEvidence);

        public string TicketFollowUpNextActionTemplate => Resolve("Copilot.TicketFollowUpNextActionTemplate", Settings.FollowUp.TicketNextActionTemplate);
        public string TicketFollowUpSimilarTemplate => Resolve("Copilot.TicketFollowUpSimilarTemplate", Settings.FollowUp.TicketSimilarTemplate);
        public string TicketFollowUpKnowledgeTemplate => Resolve("Copilot.TicketFollowUpKnowledgeTemplate", Settings.FollowUp.TicketKnowledgeTemplate);
        public string GeneralFollowUpSimilarTemplate => Resolve("Copilot.GeneralFollowUpSimilarTemplate", Settings.FollowUp.GeneralSimilarTemplate);
        public string GeneralFollowUpNextAction => Resolve("Copilot.GeneralFollowUpNextAction", Settings.FollowUp.GeneralNextAction);
        public string GeneralFollowUpKnowledge => Resolve("Copilot.GeneralFollowUpKnowledge", Settings.FollowUp.GeneralKnowledge);

        public string VerificationClarificationMessage => Resolve("Copilot.VerificationClarificationMessage", Settings.Verification.ClarificationMessage);
        public string VerificationStructuredPlanMissing => Resolve("Copilot.VerificationStructuredPlanMissing", Settings.Verification.StructuredPlanMissing);
        public string VerificationNoData => Resolve("Copilot.VerificationNoData", Settings.Verification.NoData);
        public string VerificationMissingTicketContext => Resolve("Copilot.VerificationMissingTicketContext", Settings.Verification.MissingTicketContext);
        public string VerificationTicketNotFound => Resolve("Copilot.VerificationTicketNotFound", Settings.Verification.TicketNotFound);
        public string VerificationMissingTool => Resolve("Copilot.VerificationMissingTool", Settings.Verification.MissingTool);
        public string VerificationNoAnswer => Resolve("Copilot.VerificationNoAnswer", Settings.Verification.NoAnswer);
        public string VerificationPassed => Resolve("Copilot.VerificationPassed", Settings.Verification.Passed);

        public string StepPreprocessAction => Resolve("Copilot.StepPreprocessAction", Settings.Pipeline.PreprocessAction);
        public string StepPreprocessDetailTemplate => Resolve("Copilot.StepPreprocessDetailTemplate", Settings.Pipeline.PreprocessDetailTemplate);
        public string StepNormalizeAction => Resolve("Copilot.StepNormalizeAction", Settings.Pipeline.NormalizeAction);
        public string StepNormalizeDetailTemplate => Resolve("Copilot.StepNormalizeDetailTemplate", Settings.Pipeline.NormalizeDetailTemplate);
        public string StepLoadConversationAction => Resolve("Copilot.StepLoadConversationAction", Settings.Pipeline.LoadConversationAction);
        public string StepClassifyIntentActionTemplate => Resolve("Copilot.StepClassifyIntentActionTemplate", Settings.Pipeline.ClassifyIntentActionTemplate);
        public string StepBuildPlanAction => Resolve("Copilot.StepBuildPlanAction", Settings.Pipeline.BuildPlanAction);
        public string StepBuildPlanClarificationTemplate => Resolve("Copilot.StepBuildPlanClarificationTemplate", Settings.Pipeline.BuildPlanClarificationTemplate);
        public string StepBuildPlanDetailTemplate => Resolve("Copilot.StepBuildPlanDetailTemplate", Settings.Pipeline.BuildPlanDetailTemplate);
        public string StepBuildPlanMultiQueryTemplate => Resolve("Copilot.StepBuildPlanMultiQueryTemplate", Settings.Pipeline.MultiQuerySummaryTemplate);
        public string StepExecuteActionTemplate => Resolve("Copilot.StepExecuteActionTemplate", Settings.Pipeline.ExecuteActionTemplate);
        public string StepVerifyAction => Resolve("Copilot.StepVerifyAction", Settings.Pipeline.VerifyAction);
        public string PipelineCancelledMessage => Resolve("Copilot.PipelineCancelledMessage", Settings.Pipeline.CancelledMessage);
        public string PipelineTimeoutMessage => Resolve("Copilot.PipelineTimeoutMessage", Settings.Pipeline.TimeoutMessage);
        public string PipelineErrorMessage => Resolve("Copilot.PipelineErrorMessage", Settings.Pipeline.ErrorMessage);
        public string PipelineErrorStepAction => Resolve("Copilot.PipelineErrorStepAction", Settings.Pipeline.ErrorStepAction);
        public string ClassificationFallbackStepAction => Resolve("Copilot.ClassificationFallbackStepAction", Settings.Pipeline.ClassificationFallbackStepAction);
        public string ClassificationFallbackStepDetail => Resolve("Copilot.ClassificationFallbackStepDetail", Settings.Pipeline.ClassificationFallbackStepDetail);

        private string Resolve(string key, string fallback)
        {
            var localized = _localizer.Get(key);
            return string.Equals(localized, key, StringComparison.Ordinal) ? fallback : localized;
        }
    }
}
