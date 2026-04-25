namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Approved metadata contract for Admin Copilot data access.
    /// AI can use this catalog to understand available data, but execution must still validate every plan against it.
    /// </summary>
    public class CopilotDataCatalog
    {
        public string Version { get; set; } = "1.0";
        public List<string> AllowedOutputShapes { get; set; } = [.. CopilotDataCatalogSchema.DefaultOutputShapes];
        public List<CopilotOperationDefinition> AllowedOperations { get; set; } = new();
        public List<CopilotEntityDefinition> Entities { get; set; } = new();
    }

    public class CopilotOperationDefinition
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
    }

    /// <summary>
    /// One approved business entity/table that the admin copilot may reason over.
    /// </summary>
    public class CopilotEntityDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Table { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SecurityScope { get; set; } = "AdminOnly";
        /// <summary>Maps to CopilotDataDataset enum by name. W-3: eliminates hardcoded switch.</summary>
        public string DatasetKey { get; set; } = string.Empty;
        /// <summary>Maps to CopilotAnalyticsViewKind enum by name. Eliminates ResolveViewKindAsync switch.</summary>
        public string ViewKind { get; set; } = string.Empty;
        /// <summary>
        /// When true the executor uses CopilotCatalogSqlBuilder + CopilotRawQueryExecutor
        /// (fully catalog-driven raw SQL). When false (default) the existing EF Core
        /// typed path runs. This enables a safe per-entity rollout.
        /// </summary>
        public bool SupportsRawSql { get; set; } = false;
        /// <summary>Default column for ORDER BY when the plan has no explicit sort.</summary>
        public string DefaultOrderBy { get; set; } = string.Empty;
        /// <summary>"ASC" or "DESC" — used with DefaultOrderBy.</summary>
        public string DefaultOrderDirection { get; set; } = "DESC";
        public int DefaultLimit { get; set; } = 50;
        public int MaxLimit { get; set; } = 200;
        public List<string> AllowedOperations { get; set; } = new();
        public List<string> DefaultFields { get; set; } = new();
        public CopilotLookupEnrichmentDefinition? LookupEnrichment { get; set; }
        public List<string> Aliases { get; set; } = new();
        public List<CopilotFieldDefinition> Fields { get; set; } = new();
        public List<CopilotRelationshipDefinition> Relationships { get; set; } = new();
    }

    /// <summary>
    /// Optional lookup metadata for entities whose values can be matched directly from user wording.
    /// This lets the planner enrich filters from the catalog instead of hardcoding entity-specific queries.
    /// </summary>
    public class CopilotLookupEnrichmentDefinition
    {
        public bool Enabled { get; set; } = true;
        public string ValueField { get; set; } = string.Empty;
        public string ActiveField { get; set; } = string.Empty;
        public List<string> Labels { get; set; } = new();
        public int MaxValues { get; set; } = 250;
    }

    /// <summary>
    /// One approved field with its query capabilities.
    /// These flags are the source of truth for filtering, sorting, grouping, aggregation, and display.
    /// </summary>
    public class CopilotFieldDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string? SqlExpression { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsKey { get; set; }
        public bool IsNullable { get; set; }
        public bool IsSensitive { get; set; }
        public bool IsDefaultVisible { get; set; }
        public string SecurityLevel { get; set; } = "Admin";
        public List<string> Aliases { get; set; } = new();
        public List<string> Operators { get; set; } = new();
        public List<string> Capabilities { get; set; } = new();
        public List<string> Aggregations { get; set; } = new();
        public Dictionary<string, object> AllowedValues { get; set; } = new();
    }

    /// <summary>
    /// One approved relationship edge. Dynamic joins must follow these edges only.
    /// </summary>
    public class CopilotRelationshipDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string SourceField { get; set; } = string.Empty;
        public string TargetField { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Via { get; set; } = string.Empty;
        public string ViaSourceField { get; set; } = string.Empty;
        public string ViaTargetField { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsDefaultJoin { get; set; }
    }

    public class CopilotDataCatalogValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public class CopilotDataJoinPath
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public List<CopilotRelationshipDefinition> Relationships { get; set; } = new();
    }
}
