using System.Text.Json;
using AISupportAnalysisPlatform.Models.AI;
using Microsoft.Extensions.Hosting;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Loads and exposes the approved Admin Copilot data catalog.
    /// This service is metadata-only; it does not execute queries.
    /// W-2: Catalog is cached with a 5-minute TTL. Changes to CopilotDataCatalog.json
    /// take effect within 5 minutes without an app restart.
    /// </summary>
    public class CopilotDataCatalogService
    {
        private readonly IHostEnvironment _env;
        private readonly ILogger<CopilotDataCatalogService> _logger;
        private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

        // W-2: TTL-based cache with double-checked locking for thread safety.
        private CopilotDataCatalog? _catalog;
        private DateTimeOffset _catalogLoadedAt = DateTimeOffset.MinValue;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
        private readonly SemaphoreSlim _loadLock = new(1, 1);

        public CopilotDataCatalogService(IHostEnvironment env, ILogger<CopilotDataCatalogService> logger)
        {
            _env = env;
            _logger = logger;
        }

        public async Task<CopilotDataCatalog> GetCatalogAsync()
        {
            // Fast path: cache still fresh
            if (_catalog != null && DateTimeOffset.UtcNow - _catalogLoadedAt < CacheTtl)
                return _catalog;

            await _loadLock.WaitAsync();
            try
            {
                // Double-check inside the lock
                if (_catalog != null && DateTimeOffset.UtcNow - _catalogLoadedAt < CacheTtl)
                    return _catalog;

                var path = Path.Combine(_env.ContentRootPath, "Services", "AI", "Copilot", "Data", "CopilotDataCatalog.json");

                if (!File.Exists(path))
                {
                    _logger.LogError("CopilotDataCatalog.json not found at {Path}", path);
                    return new CopilotDataCatalog();
                }

                var json = await File.ReadAllTextAsync(path);
                var loaded = JsonSerializer.Deserialize<CopilotDataCatalog>(json, _options) ?? new CopilotDataCatalog();

                _catalog = loaded;
                _catalogLoadedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("CopilotDataCatalog loaded — {EntityCount} entities. Next reload after {Expiry:HH:mm:ss}.",
                    loaded.Entities.Count, _catalogLoadedAt.Add(CacheTtl).LocalDateTime);

                return _catalog;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load CopilotDataCatalog. Using empty or stale catalog.");
                return _catalog ?? new CopilotDataCatalog();
            }
            finally
            {
                _loadLock.Release();
            }
        }

        /// <summary>Forces the next call to reload from disk, bypassing the TTL.</summary>
        public void InvalidateCache()
        {
            _catalogLoadedAt = DateTimeOffset.MinValue;
            _logger.LogInformation("CopilotDataCatalog cache invalidated manually.");
        }

        public async Task<string> GetCatalogAsJsonContextAsync()
        {
            var catalog = await GetCatalogAsync();
            return JsonSerializer.Serialize(catalog, _options);
        }

        public async Task<CopilotEntityDefinition?> FindEntityAsync(string entityName)
        {
            var catalog = await GetCatalogAsync();
            return catalog.Entities.FirstOrDefault(e =>
                e.Name.Equals(entityName, StringComparison.OrdinalIgnoreCase) ||
                e.Table.Equals(entityName, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<CopilotEntityDefinition?> FindEntityByAliasAsync(string alias)
        {
            var catalog = await GetCatalogAsync();
            return catalog.Entities.FirstOrDefault(e =>
                e.Name.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
                e.Table.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
                e.Aliases.Any(a => a.Equals(alias, StringComparison.OrdinalIgnoreCase)));
        }

        public async Task<CopilotFieldDefinition?> FindFieldAsync(string entityName, string fieldName)
        {
            var entity = await FindEntityAsync(entityName);
            return entity?.Fields.FirstOrDefault(f =>
                f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) ||
                f.Aliases.Any(a => a.Equals(fieldName, StringComparison.OrdinalIgnoreCase)));
        }

        public async Task<bool> CanUseFieldForAsync(string entityName, string fieldName, string capability)
        {
            var field = await FindFieldAsync(entityName, fieldName);
            return field?.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase) == true;
        }

        public async Task<CopilotDataJoinPath?> ResolveJoinPathAsync(string sourceEntity, string targetEntity, int maxDepth = 4)
        {
            var catalog = await GetCatalogAsync();
            var source = catalog.Entities.FirstOrDefault(e => e.Name.Equals(sourceEntity, StringComparison.OrdinalIgnoreCase));
            var target = catalog.Entities.FirstOrDefault(e => e.Name.Equals(targetEntity, StringComparison.OrdinalIgnoreCase));
            if (source == null || target == null) return null;

            var queue = new Queue<(string Entity, List<CopilotRelationshipDefinition> Path)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source.Name };
            queue.Enqueue((source.Name, []));

            while (queue.Count > 0)
            {
                var (currentName, path) = queue.Dequeue();
                if (path.Count > maxDepth) continue;

                if (currentName.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
                    return new CopilotDataJoinPath { Source = source.Name, Target = target.Name, Relationships = path };

                var current = catalog.Entities.FirstOrDefault(e => e.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase));
                if (current == null) continue;

                foreach (var rel in current.Relationships)
                {
                    if (!visited.Add(rel.Target)) continue;
                    queue.Enqueue((rel.Target, [.. path, rel]));
                }
            }

            return null;
        }

        public async Task<IReadOnlyList<CopilotEntityDefinition>> GetEntitiesAsync()
        {
            var catalog = await GetCatalogAsync();
            return catalog.Entities;
        }
        public string NormalizeAggregationType(string? aggregationType)
        {
            if (string.IsNullOrWhiteSpace(aggregationType)) return "count";

            return aggregationType.Trim().ToLowerInvariant() switch
            {
                "count" => "count",
                "max" or "maximum" or "highest" => "max",
                "min" or "minimum" or "lowest" => "min",
                "avg" or "average" or "mean" => "average",
                "sum" or "total sum" => "sum",
                _ => aggregationType.Trim().ToLowerInvariant()
            };
        }

        public string GetAggregationLabel(string? aggregationType) => NormalizeAggregationType(aggregationType) switch
        {
            "count" => "Count",
            "max" => "Maximum",
            "min" => "Minimum",
            "average" => "Average",
            "sum" => "Sum",
            _ => aggregationType ?? "Count"
        };
    }
}
