using System.Text.Json.Serialization;

namespace AISupportAnalysisPlatform.Models.AI
{
    public class BilingualRetrievalBenchmark
    {
        public string Version { get; set; } = "1.0";
        public DateTime CreatedOnUtc { get; set; } = DateTime.UtcNow;
        public List<RetrievalBenchmarkCase> Cases { get; set; } = new();
    }

    public class RetrievalBenchmarkCase
    {
        public string Id { get; set; } = string.Empty;
        public string Bucket { get; set; } = string.Empty;
        public string QueryLanguage { get; set; } = string.Empty;
        public string QueryText { get; set; } = string.Empty;
        public int? SourceTicketId { get; set; }
        public int Count { get; set; } = 5;
        public bool IncludeAllStatuses { get; set; }
        public List<int> StatusIds { get; set; } = new();
        public List<int> ExpectedTicketIds { get; set; } = new();
        public string Intent { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsValid =>
            !string.IsNullOrWhiteSpace(Id) &&
            !string.IsNullOrWhiteSpace(Bucket) &&
            !string.IsNullOrWhiteSpace(QueryText);
    }

    public class BilingualRetrievalBenchmarkRunResult
    {
        public string Version { get; set; } = "1.0";
        public DateTime RunOnUtc { get; set; } = DateTime.UtcNow;
        public int TotalCases { get; set; }
        public int EvaluatedCases { get; set; }
        public int HitCases { get; set; }
        public List<RetrievalBenchmarkBucketResult> Buckets { get; set; } = new();
        public List<RetrievalBenchmarkCaseResult> Cases { get; set; } = new();
    }

    public class RetrievalBenchmarkBucketResult
    {
        public string Bucket { get; set; } = string.Empty;
        public int TotalCases { get; set; }
        public int EvaluatedCases { get; set; }
        public int HitCases { get; set; }
    }

    public class RetrievalBenchmarkCaseResult
    {
        public string Id { get; set; } = string.Empty;
        public string Bucket { get; set; } = string.Empty;
        public string QueryLanguage { get; set; } = string.Empty;
        public string QueryText { get; set; } = string.Empty;
        public string Intent { get; set; } = string.Empty;
        public int? SourceTicketId { get; set; }
        public bool IsSourceTicketMissing { get; set; }
        public bool HasExpectation { get; set; }
        public bool? IsHit { get; set; }
        public List<int> ExpectedTicketIds { get; set; } = new();
        public List<int> MissingExpectedTicketIds { get; set; } = new();
        public List<int> ReturnedTicketIds { get; set; } = new();
        public List<RetrievalBenchmarkMatchResult> Matches { get; set; } = new();
    }

    public class RetrievalBenchmarkMatchResult
    {
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public double Score { get; set; }
    }

    public class RetrievalBenchmarkValidationResult
    {
        public string Version { get; set; } = "1.0";
        public DateTime ValidatedOnUtc { get; set; } = DateTime.UtcNow;
        public int TotalCases { get; set; }
        public int CasesWithWarnings { get; set; }
        public List<RetrievalBenchmarkValidationCase> Cases { get; set; } = new();
    }

    public class RetrievalBenchmarkValidationCase
    {
        public string Id { get; set; } = string.Empty;
        public string Bucket { get; set; } = string.Empty;
        public int? SourceTicketId { get; set; }
        public bool IsSourceTicketMissing { get; set; }
        public List<int> ExpectedTicketIds { get; set; } = new();
        public List<int> MissingExpectedTicketIds { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
