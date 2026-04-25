using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class CopilotVerificationEngine : ICopilotVerificationEngine
    {
        private readonly ICopilotKnowledgeEngine _knowledge;

        public CopilotVerificationEngine(ICopilotKnowledgeEngine knowledge)
        {
            _knowledge = knowledge;
        }

        public CopilotVerificationResult Verify(CopilotExecutionPlan plan, CopilotExecutionResult result)
        {
            // Note: plan.RequiresClarification is handled in CopilotExecutionEngine before this method
            // is called. This check is intentionally absent here to avoid generating duplicate trace steps.

            // Intent-specific data integrity checks
            var intent = plan.Decision.Intent;

            if (intent == CopilotIntentKind.DataQuery)
            {
                if (result.DynamicQueryPlan == null && !result.StructuredQueryResults.Any())
                {
                    return Failed(_knowledge.Messages.VerificationStructuredPlanMissing);
                }

                if ((result.ResultCount ?? 0) == 0 && result.EvidenceStrength != EvidenceStrength.High)
                {
                    return new CopilotVerificationResult
                    {
                        Status = CopilotVerificationStatus.Passed,
                        Message = _knowledge.Messages.VerificationNoData,
                        EvidenceStrength = EvidenceStrength.Moderate
                    };
                }
            }

            if (intent == CopilotIntentKind.ExternalToolQuery && (string.IsNullOrWhiteSpace(result.UsedTool) || result.UsedTool == "none"))
            {
                return Failed(_knowledge.Messages.VerificationMissingTool);
            }

            // 3. Hallucination and ambiguity checks
            if (string.IsNullOrWhiteSpace(result.Answer))
            {
                return Failed(_knowledge.Messages.VerificationNoAnswer);
            }

            // If the model gave a very short answer to a high-confidence intent without using the expected tools/data, mark as weak.
            if (result.EvidenceStrength == EvidenceStrength.Weak && 
                result.Answer.Length < 50 && 
                plan.Decision.Confidence == RoutingConfidence.High &&
                intent != CopilotIntentKind.GeneralChat)
            {
                return new CopilotVerificationResult
                {
                    Status = CopilotVerificationStatus.NeedsClarification,
                    Message = _knowledge.Messages.VerificationClarificationMessage,
                    EvidenceStrength = EvidenceStrength.Weak
                };
            }

            // 4. Final approval
            return new CopilotVerificationResult
            {
                Status = CopilotVerificationStatus.Passed,
                Message = _knowledge.Messages.VerificationPassed,
                EvidenceStrength = result.EvidenceStrength
            };
        }

        private static CopilotVerificationResult Failed(string message) => new()
        {
            Status = CopilotVerificationStatus.Failed,
            Message = message,
            EvidenceStrength = EvidenceStrength.Weak
        };
    }
}
