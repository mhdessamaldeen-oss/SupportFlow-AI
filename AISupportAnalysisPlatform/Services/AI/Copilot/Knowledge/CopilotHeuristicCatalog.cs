using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI
{
    public sealed class CopilotHeuristicCatalog
    {
        public static readonly HashSet<string> EnglishStopWords = new(StringComparer.Ordinal)
        {
            "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it",
            "of", "on", "or", "that", "the", "this", "to", "was", "were", "with"
        };

        public static readonly HashSet<string> ArabicStopWords = new(StringComparer.Ordinal)
        {
            "في", "من", "على", "الى", "إلى", "عن", "مع", "تم", "هذا", "هذه", "ذلك", "تلك",
            "هناك", "عند", "بعد", "قبل", "كل", "كان", "كانت", "هو", "هي", "ثم", "او", "أو"
        };

        public static readonly string[] KnowledgeBaseIndexedFolders = ["SOPs", "KnownIssues", "Troubleshooting", "ReleaseNotes"];
        
        public const float KnowledgeBaseScoreThreshold = 0.25f;
        public const float SemanticSearchBaseThreshold = 0.05f;
        public const float SemanticSearchMaxThreshold = 0.40f;

        public static readonly List<CopilotStructuredSubPlan> DashboardSummaryPlans =
        [
            new CopilotStructuredSubPlan
            {
                Label = "Top entities by volume",
                QueryText = "show top 5 entities by ticket count",
                QueryPlan = new AdminCopilotDynamicTicketQueryPlan
                {
                    TargetView = "EntitySummary",
                    Intent = DynamicQueryIntent.List,
                    Summary = "Top entities by ticket count for the reporting dashboard.",
                    MaxResults = 5,
                    SortBy = "TotalTickets",
                    SortDirection = SortDirection.Desc,
                    SelectedColumns = ["EntityName", "TotalTickets", "OpenTickets", "ResolvedTickets", "SlaBreachRate"]
                }
            },
            new CopilotStructuredSubPlan
            {
                Label = "Highest SLA breach areas",
                QueryText = "show top 5 entities by SLA breach rate",
                QueryPlan = new AdminCopilotDynamicTicketQueryPlan
                {
                    TargetView = "EntitySummary",
                    Intent = DynamicQueryIntent.List,
                    Summary = "Entities with the highest SLA breach rate in the reporting dashboard.",
                    MaxResults = 5,
                    SortBy = "SlaBreachRate",
                    SortDirection = SortDirection.Desc,
                    SelectedColumns = ["EntityName", "SlaBreachRate", "SlaBreaches", "TotalTickets"]
                }
            },
            new CopilotStructuredSubPlan
            {
                Label = "Top agents by resolution",
                QueryText = "show top 5 agents by resolved tickets",
                QueryPlan = new AdminCopilotDynamicTicketQueryPlan
                {
                    TargetView = "AgentSummary",
                    Intent = DynamicQueryIntent.List,
                    Summary = "Top agents by resolved ticket volume for the reporting dashboard.",
                    MaxResults = 5,
                    SortBy = "TotalResolved",
                    SortDirection = SortDirection.Desc,
                    SelectedColumns = ["AgentName", "EntityName", "TotalResolved", "CurrentOpenAssigned", "AvgResolutionHours"]
                }
            }
        ];

        public IReadOnlyList<string> GreetingPhrases { get; } =
        [
            "hello", "hi", "hey", "thanks", "thank you", "good morning", "good evening", "good afternoon",
            "مرحبا", "السلام عليكم", "اهلا", "شكرا"
        ];

        public IReadOnlyList<string> CapabilityQuestions { get; } =
        [
            "who are you", "what can you do"
        ];

        public IReadOnlyList<string> AnalyticsPhrases { get; } =
        [
            "count", "how much", "how many", "total", "sum", "list", "show", "find", "compare", "group", "split", "sort", "top", "latest", "recent",
            "last", "first", "summary", "breakdown", "table", "row", "rows", "column", "columns", "highest",
            "lowest", "open", "closed", "resolved", "pending", "per", "by", "percent", "percentage", "rate", "trend",
            "كم", "كمية", "اعرض", "عدد", "مقارنة", "حسب", "الأعلى", "الأحدث", "آخر", "أعمدة", "مفتوحة", "مغلقة", "محلولة", "إجمالي"
        ];

        public IReadOnlyList<string> TicketDomainPhrases { get; } =
        [
            "ticket", "tickets", "status", "priority", "entity", "entities", "agent", "agents", "sla", "breach",
            "breaches", "assigned", "resolved", "review", "support", "request", "requests", "issue", "issues", "data",
            "department", "dept", "organization", "org",
            "تذكرة", "تذاكر", "حالة", "أولوية", "جهة", "جهات", "موظف", "موظفين", "مراجعة", "دعم", "طلب", "طلبات", "مشكلة", "مشاكل",
            "قسم", "مؤسسة"
        ];

        public IReadOnlyList<string> KnowledgePhrases { get; } =
        [
            "how to", "why", "policy", "procedure", "guide", "step", "steps", "troubleshoot", "troubleshooting",
            "root cause", "known issue", "release note", "release notes", "explain", "reason", "cause",
            "كيف", "لماذا", "سياسة", "إجراء", "دليل", "سبب", "شرح", "مشكلة"
        ];

        public IReadOnlyList<string> FollowUpPhrases { get; } =
        [
            "same", "previous", "continue", "only", "now", "this", "that", "narrow", "filter", "instead",
            "نفس", "السابق", "كمل", "الآن", "هذا", "هذاك"
        ];

        public IReadOnlyList<string> ConversationFollowUpPhrases { get; } =
        [
            "continue", "same", "that", "those", "them", "it", "also", "now", "then", "again",
            "previous", "last one", "first one", "second one", "only", "just", "another",
            "كمل", "نفس", "هذا", "هذه", "هذول", "السابق", "برضه", "أيضا"
        ];

        public IReadOnlyList<string> ConversationDeltaPhrases { get; } =
        [
            "sort", "filter", "top", "latest", "recent", "first", "last", "group", "split", "compare",
            "only", "just", "column", "columns", "limit", "breakdown", "summary",
            "رتب", "فلتر", "الأحدث", "الأعلى", "أعمدة", "ملخص"
        ];

        public IReadOnlyList<string> TicketFollowUpSignals { get; } =
        [
            "why", "reason", "cause", "resolution", "next action", "similar", "update", "history", "root cause",
            "why pending", "status update",
            "لماذا", "سبب", "حل", "إجراء", "مشابه", "تحديث"
        ];

        public IReadOnlyList<string> MultiPartSeparators { get; } =
        [
            "and", "also", "then", "plus", "ثم", "و", "كذلك"
        ];

        public IReadOnlyList<string> ToolStopWords { get; } =
        [
            "what", "which", "when", "where", "tell", "show", "give", "find", "get", "latest", "current",
            "today", "please", "need", "want", "with", "from", "into", "about", "this", "that", "have",
            "على", "عن", "في", "من", "هل", "ما", "ماذا", "اعرض", "اعطني", "ابحث"
        ];

        public IReadOnlyList<string> ToolLeadingTerms { get; } =
        [
            "in", "for", "of", "about", "from", "at", "في", "عن", "من", "على"
        ];

        public IReadOnlyList<string> RouterGreetingPhrases { get; } =
        [
            "hello", "hi", "hey", "good morning", "good evening", "good afternoon", "who are you", "what can you do",
            "help", "مرحبا", "السلام عليكم", "اهلا", "شو تقدر تسوي"
        ];

        public IReadOnlyList<string> RouterDataQueryPhrases { get; } =
        [
            "ticket count", "how many tickets", "total tickets", "count of tickets", "longest title", "shortest title",
            "last ticket", "latest ticket", "recent tickets", "show tickets", "list tickets", "find ticket",
            "tickets for", "tickets from", "tickets by", "open tickets", "closed tickets", "resolved tickets",
            "critical tickets", "كم عدد التذاكر", "اعرض التذاكر", "آخر التذاكر", "التذاكر المفتوحة"
        ];

        public IReadOnlyList<string> RouterActionPhrases { get; } =
        [
            "assign", "transfer", "set status", "change priority", "add comment", "post note", "escalate", "resolve",
            "close ticket", "تحويل", "تغيير الحالة", "إضافة تعليق"
        ];

        public IReadOnlyDictionary<string, string> SynonymMap { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tkt"] = "ticket",
            ["tkts"] = "tickets",
            ["stats"] = "summary",
            ["stat"] = "summary",
            ["statistics"] = "summary",
            ["gimme"] = "show",
            ["display"] = "show",
            ["give me"] = "show",
            ["show me"] = "show",
            ["howmany"] = "how many",
            ["how-many"] = "how many",
            ["howmuch"] = "how much",
            ["how-much"] = "how much",
            ["analyze"] = "summary",
            ["explain"] = "summary",
            ["details"] = "summary",
            ["broken"] = "troubleshoot",
            ["owner"] = "assigned user",
            ["dept"] = "department",
            ["org"] = "organization",
            // Arabic Dialect/Common Synonyms
            ["مسكرة"] = "مغلقة",
            ["مقفولة"] = "مغلقة",
            ["مفتوحة"] = "فتح",
            ["مو مسكرة"] = "مفتوحة",
            ["مش مسكرة"] = "مفتوحة",
            ["غير مسكرة"] = "مفتوحة",
            ["سوي"] = "اعمل",
            ["هات"] = "اعرض",
            ["طلع"] = "اعرض",
            ["شوف"] = "ابحث"
        };

        public IReadOnlyList<string> InjectionPatterns { get; } =
        [
            "ignore all previous instructions", "ignore the above", "disregard all previous", 
            "system:", "human:", "ai:", "assistant:", "user:", "prompt injection",
            "you are now", "instead of being", "forget everything", "new rules"
        ];
    }
}
