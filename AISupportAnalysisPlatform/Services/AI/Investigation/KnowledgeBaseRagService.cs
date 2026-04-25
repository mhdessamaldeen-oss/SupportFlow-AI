using AISupportAnalysisPlatform.Models.AI;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class KnowledgeBaseRagService
    {
        private static readonly string[] IndexedFolders = CopilotHeuristicCatalog.KnowledgeBaseIndexedFolders;
        private readonly IHostEnvironment _environment;
        private readonly ITicketEmbeddingEngine _embeddingEngine;
        private readonly ILogger<KnowledgeBaseRagService> _logger;
        private static readonly ConcurrentDictionary<string, CachedDocument> DocumentCache = new(StringComparer.OrdinalIgnoreCase);

        public KnowledgeBaseRagService(
            IHostEnvironment environment,
            ITicketEmbeddingEngine embeddingEngine,
            ILogger<KnowledgeBaseRagService> logger)
        {
            _environment = environment;
            _embeddingEngine = embeddingEngine;
            _logger = logger;
        }

        public string KnowledgeBaseRootPath => Path.Combine(_environment.ContentRootPath, "KnowledgeBase");

        public IReadOnlyList<string> GetManagedFolders() => IndexedFolders;

        public int GetDocumentCount()
        {
            if (!Directory.Exists(KnowledgeBaseRootPath))
            {
                return 0;
            }

            return Directory
                .EnumerateFiles(KnowledgeBaseRootPath, "*.*", SearchOption.AllDirectories)
                .Count(IsIndexedDocument);
        }

        public IReadOnlyList<KnowledgeBaseDocumentInfo> GetManagedDocuments()
        {
            if (!Directory.Exists(KnowledgeBaseRootPath))
            {
                return [];
            }

            return Directory
                .EnumerateFiles(KnowledgeBaseRootPath, "*.*", SearchOption.AllDirectories)
                .Where(IsIndexedDocument)
                .Select(filePath =>
                {
                    var fileInfo = new FileInfo(filePath);
                    var relativePath = Path.GetRelativePath(KnowledgeBaseRootPath, filePath).Replace('\\', '/');
                    var category = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

                    return new KnowledgeBaseDocumentInfo
                    {
                        Category = category,
                        FileName = fileInfo.Name,
                        RelativePath = relativePath,
                        SizeBytes = fileInfo.Length,
                        UpdatedOnUtc = fileInfo.LastWriteTimeUtc
                    };
                })
                .OrderBy(d => d.Category)
                .ThenBy(d => d.FileName)
                .ToList();
        }

        public bool IsManagedCategory(string category) =>
            IndexedFolders.Contains(category, StringComparer.OrdinalIgnoreCase);

        public string GetCategoryDirectory(string category) =>
            Path.Combine(KnowledgeBaseRootPath, category);

        public void InvalidateDocument(string filePath)
        {
            DocumentCache.TryRemove(filePath, out _);
        }

        public Task<List<KnowledgeBaseChunkMatch>> SearchAsync(string queryText, int count = 3, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(queryText))
            {
                return Task.FromResult(new List<KnowledgeBaseChunkMatch>());
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(KnowledgeBaseRootPath))
            {
                _logger.LogInformation("Knowledge base folder not found at {Path}", KnowledgeBaseRootPath);
                return Task.FromResult(new List<KnowledgeBaseChunkMatch>());
            }

            var normalizedQuery = NormalizeText(queryText);
            var queryVector = _embeddingEngine.GenerateEmbedding(normalizedQuery);
            if (queryVector.Length == 0)
            {
                return Task.FromResult(new List<KnowledgeBaseChunkMatch>());
            }
            var queryTerms = ExtractTerms(normalizedQuery);

            var chunks = new List<CachedChunk>();
            foreach (var filePath in Directory.EnumerateFiles(KnowledgeBaseRootPath, "*.*", SearchOption.AllDirectories).Where(IsIndexedDocument))
            {
                chunks.AddRange(GetCachedChunks(filePath));
            }

            var results = chunks
                .Where(c => c.Vector.Length == queryVector.Length)
                .Select(c => new KnowledgeBaseChunkMatch
                {
                    DocumentName = c.DocumentName,
                    SectionTitle = c.SectionTitle,
                    SourcePath = c.SourcePath,
                    Excerpt = BuildExcerpt(c.Text),
                    VectorScore = Math.Max(0f, CosineSimilarity(queryVector, c.Vector)),
                    LexicalScore = ComputeLexicalScore(queryTerms, c.Terms)
                })
                .Select(m =>
                {
                    m.Score = Math.Clamp((m.VectorScore * 0.78f) + (m.LexicalScore * 0.22f), 0f, 1f);
                    return m;
                })
                .Where(m => m.Score >= CopilotHeuristicCatalog.KnowledgeBaseScoreThreshold)
                .OrderByDescending(m => m.Score)
                .Take(count)
                .ToList();

            if (results.Count == 0 && count > 0)
            {
                _logger.LogInformation("Semantic search found no matches above confidence threshold ({Threshold}) for query: {Query}", CopilotHeuristicCatalog.KnowledgeBaseScoreThreshold, queryText);
            }

            return Task.FromResult(results);
        }

        private IEnumerable<CachedChunk> GetCachedChunks(string filePath)
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
            if (DocumentCache.TryGetValue(filePath, out var cached) && cached.LastWriteUtc == lastWriteUtc)
            {
                return cached.Chunks;
            }

            var built = BuildDocument(filePath, lastWriteUtc);
            DocumentCache[filePath] = built;
            return built.Chunks;
        }

        private CachedDocument BuildDocument(string filePath, DateTime lastWriteUtc)
        {
            var text = File.ReadAllText(filePath);
            var documentName = Path.GetFileNameWithoutExtension(filePath);
            var relativePath = Path.GetRelativePath(KnowledgeBaseRootPath, filePath).Replace('\\', '/');
            var chunks = SplitIntoChunks(text, documentName)
                .Select(chunk =>
                {
                    var normalizedText = NormalizeText(chunk.Text);
                    var vector = _embeddingEngine.GenerateEmbedding(normalizedText);
                    return new CachedChunk
                    {
                        DocumentName = documentName,
                        SectionTitle = chunk.SectionTitle,
                        Text = chunk.Text,
                        SourcePath = relativePath,
                        Vector = vector,
                        Terms = ExtractTerms(normalizedText)
                    };
                })
                .Where(c => c.Vector.Length > 0)
                .ToList();

            _logger.LogInformation("Indexed {ChunkCount} knowledge chunks from {Document}", chunks.Count, relativePath);

            return new CachedDocument
            {
                LastWriteUtc = lastWriteUtc,
                Chunks = chunks
            };
        }

        private static IEnumerable<(string SectionTitle, string Text)> SplitIntoChunks(string rawText, string documentName)
        {
            var sectionTitle = documentName;
            var sectionBuffer = new StringBuilder();

            foreach (var line in rawText.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#"))
                {
                    foreach (var chunk in FlushSection(sectionTitle, sectionBuffer.ToString()))
                    {
                        yield return chunk;
                    }

                    sectionTitle = trimmed.TrimStart('#', ' ');
                    sectionBuffer.Clear();
                    continue;
                }

                sectionBuffer.AppendLine(trimmed);
            }

            foreach (var chunk in FlushSection(sectionTitle, sectionBuffer.ToString()))
            {
                yield return chunk;
            }
        }

        private static IEnumerable<(string SectionTitle, string Text)> FlushSection(string sectionTitle, string rawSectionText)
        {
            var paragraphs = rawSectionText
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            if (paragraphs.Count == 0)
            {
                yield break;
            }

            var buffer = new StringBuilder();
            foreach (var paragraph in paragraphs)
            {
                if (buffer.Length > 0 && buffer.Length + paragraph.Length > 700)
                {
                    yield return (sectionTitle, buffer.ToString().Trim());
                    buffer.Clear();
                }

                if (buffer.Length > 0)
                {
                    buffer.AppendLine();
                    buffer.AppendLine();
                }

                buffer.Append(paragraph);
            }

            if (buffer.Length > 0)
            {
                yield return (sectionTitle, buffer.ToString().Trim());
            }
        }

        private static string BuildExcerpt(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var trimmed = text.Trim();
            return trimmed.Length <= 280 ? trimmed : $"{trimmed[..277]}...";
        }

        private static HashSet<string> ExtractTerms(string normalizedText)
        {
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            return normalizedText
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsUsefulTerm)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool IsUsefulTerm(string term)
        {
            if (term.Length < 2)
            {
                return false;
            }

            if (CopilotHeuristicCatalog.EnglishStopWords.Contains(term) || CopilotHeuristicCatalog.ArabicStopWords.Contains(term))
            {
                return false;
            }

            return term.Any(char.IsLetterOrDigit);
        }

        private static float ComputeLexicalScore(IReadOnlyCollection<string> queryTerms, IReadOnlyCollection<string> candidateTerms)
        {
            if (queryTerms.Count == 0 || candidateTerms.Count == 0)
            {
                return 0f;
            }

            var candidateTermSet = candidateTerms as HashSet<string> ?? candidateTerms.ToHashSet(StringComparer.Ordinal);
            var matchedTerms = queryTerms.Count(candidateTermSet.Contains);
            return matchedTerms == 0 ? 0f : Math.Clamp((float)matchedTerms / queryTerms.Count, 0f, 1f);
        }

        private bool IsIndexedDocument(string filePath)
        {
            if (!IsSupportedDocument(filePath))
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(KnowledgeBaseRootPath, filePath).Replace('\\', '/');
            var topFolder = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return topFolder != null && IndexedFolders.Contains(topFolder, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsSupportedDocument(string filePath)
        {
            var extension = Path.GetExtension(filePath);
            return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length);
            var previousWasWhitespace = false;

            foreach (var ch in text.Normalize(NormalizationForm.FormKC))
            {
                if (char.IsControl(ch))
                {
                    continue;
                }

                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                var lowered = char.ToLower(ch, CultureInfo.InvariantCulture);
                var normalized = lowered switch
                {
                    'أ' or 'إ' or 'آ' => 'ا',
                    'ؤ' => 'و',
                    'ئ' => 'ي',
                    'ة' => 'ه',
                    'ى' => 'ي',
                    '٠' or '۰' => '0',
                    '١' or '۱' => '1',
                    '٢' or '۲' => '2',
                    '٣' or '۳' => '3',
                    '٤' or '۴' => '4',
                    '٥' or '۵' => '5',
                    '٦' or '۶' => '6',
                    '٧' or '۷' => '7',
                    '٨' or '۸' => '8',
                    '٩' or '۹' => '9',
                    '،' => ',',
                    '؛' => ';',
                    '؟' => '?',
                    '\u0640' => ' ',
                    _ => lowered
                };

                if (char.IsPunctuation(normalized) || char.IsSeparator(normalized) || char.IsWhiteSpace(normalized))
                {
                    if (previousWasWhitespace)
                    {
                        continue;
                    }

                    sb.Append(' ');
                    previousWasWhitespace = true;
                    continue;
                }

                sb.Append(normalized);
                previousWasWhitespace = false;
            }

            return sb.ToString().Trim();
        }

        private static float CosineSimilarity(float[] vectorA, float[] vectorB)
        {
            if (vectorA.Length != vectorB.Length || vectorA.Length == 0)
            {
                return 0f;
            }

            float dotProduct = 0;
            float normA = 0;
            float normB = 0;

            for (var i = 0; i < vectorA.Length; i++)
            {
                dotProduct += vectorA[i] * vectorB[i];
                normA += vectorA[i] * vectorA[i];
                normB += vectorB[i] * vectorB[i];
            }

            if (normA == 0 || normB == 0)
            {
                return 0f;
            }

            return dotProduct / ((float)Math.Sqrt(normA) * (float)Math.Sqrt(normB));
        }

        private sealed class CachedDocument
        {
            public DateTime LastWriteUtc { get; init; }
            public List<CachedChunk> Chunks { get; init; } = new();
        }

        private sealed class CachedChunk
        {
            public string DocumentName { get; init; } = "";
            public string SectionTitle { get; init; } = "";
            public string Text { get; init; } = "";
            public string SourcePath { get; init; } = "";
            public float[] Vector { get; init; } = Array.Empty<float>();
            public HashSet<string> Terms { get; init; } = new(StringComparer.Ordinal);
        }
    }
}
