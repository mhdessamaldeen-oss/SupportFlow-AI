using AISupportAnalysisPlatform.Models.AI;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Validates the metadata catalog before it is used by AI planning or dynamic query execution.
    /// The goal is to fail fast on broken entity, field, relationship, or capability definitions.
    /// </summary>
    public class CopilotDataCatalogValidatorService
    {
        private readonly CopilotDataCatalogService _catalogService;
        private readonly IServiceScopeFactory _scopeFactory;

        public CopilotDataCatalogValidatorService(CopilotDataCatalogService catalogService, IServiceScopeFactory scopeFactory)
        {
            _catalogService = catalogService;
            _scopeFactory = scopeFactory;
        }

        public async Task<CopilotDataCatalogValidationResult> ValidateAsync()
        {
            var catalog = await _catalogService.GetCatalogAsync();
            
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AISupportAnalysisPlatform.Data.ApplicationDbContext>();
            
            return Validate(catalog, dbContext);
        }

        public CopilotDataCatalogValidationResult Validate(CopilotDataCatalog catalog, AISupportAnalysisPlatform.Data.ApplicationDbContext dbContext)
        {
            var result = new CopilotDataCatalogValidationResult();
            if (!catalog.Entities.Any())
            {
                result.Errors.Add("Catalog has no entities.");
                return result;
            }

            if (!catalog.AllowedOutputShapes.Any())
            {
                result.Warnings.Add("Catalog has no allowed output shapes. Default output shapes will be assumed.");
            }
            else
            {
                foreach (var outputShape in catalog.AllowedOutputShapes)
                {
                    if (!CopilotDataCatalogSchema.IsSupportedOutputShape(outputShape))
                    {
                        result.Errors.Add($"Catalog has unsupported output shape '{outputShape}'.");
                    }
                }
            }

            var entityNames = new HashSet<string>(catalog.Entities.Select(entity => entity.Name), StringComparer.OrdinalIgnoreCase);
            var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in catalog.Entities)
            {
                ValidateEntity(entity, catalog, entityNames, tableNames, result, dbContext);
            }

            return result;
        }

        private static void ValidateEntity(
            CopilotEntityDefinition entity,
            CopilotDataCatalog catalog,
            HashSet<string> entityNames,
            HashSet<string> tableNames,
            CopilotDataCatalogValidationResult result,
            AISupportAnalysisPlatform.Data.ApplicationDbContext dbContext)
        {
            if (string.IsNullOrWhiteSpace(entity.Name))
            {
                result.Errors.Add("Entity is missing Name.");
            }

            if (string.IsNullOrWhiteSpace(entity.Table))
            {
                result.Errors.Add($"Entity '{entity.Name}' is missing Table.");
            }
            else if (!CopilotDataCatalogSchema.IsSafeIdentifier(entity.Table))
            {
                result.Errors.Add($"Entity '{entity.Name}' table '{entity.Table}' is not a safe SQL identifier.");
            }
            else if (!tableNames.Add(entity.Table))
            {
                result.Warnings.Add($"Table '{entity.Table}' is mapped by more than one entity.");
            }

            if (!entity.Fields.Any(field => field.IsKey))
            {
                result.Errors.Add($"Entity '{entity.Name}' has no key field.");
            }

            if (!entity.DefaultFields.Any())
            {
                result.Warnings.Add($"Entity '{entity.Name}' has no default fields.");
            }

            foreach (var operation in entity.AllowedOperations)
            {
                if (!CopilotDataCatalogSchema.IsSupportedOperation(operation))
                {
                    result.Errors.Add($"Entity '{entity.Name}' has unsupported operation '{operation}'.");
                }
            }

            var fieldNames = new HashSet<string>(entity.Fields.Select(field => field.Name), StringComparer.OrdinalIgnoreCase);
            var allowedProjectedFields = BuildAllowedProjectedFields(entity, catalog, fieldNames);
            foreach (var defaultField in entity.DefaultFields)
            {
                if (!allowedProjectedFields.Contains(defaultField))
                {
                    result.Errors.Add($"Entity '{entity.Name}' default field '{defaultField}' is not defined.");
                }
            }

            // Physical Schema Guard: Check if table exists in DB
            var efEntity = dbContext.Model.GetEntityTypes().FirstOrDefault(e => 
                (e.GetTableName() ?? e.GetViewName())?.Equals(entity.Table, StringComparison.OrdinalIgnoreCase) == true);

            if (efEntity == null)
            {
                result.Errors.Add($"Entity '{entity.Name}' table '{entity.Table}' does not exist in the database schema.");
            }

            foreach (var field in entity.Fields)
            {
                ValidateField(entity.Name, field, result, efEntity);
            }

            if (entity.LookupEnrichment != null)
            {
                ValidateLookupEnrichment(entity.Name, entity.LookupEnrichment, fieldNames, result);
            }

            foreach (var relationship in entity.Relationships)
            {
                ValidateRelationship(entity.Name, relationship, entityNames, fieldNames, result);
            }
        }

        private static void ValidateField(
            string entityName,
            CopilotFieldDefinition field,
            CopilotDataCatalogValidationResult result,
            Microsoft.EntityFrameworkCore.Metadata.IEntityType? efEntity)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                result.Errors.Add($"Entity '{entityName}' has a field without a name.");
            }
            else if (!CopilotDataCatalogSchema.IsSafeIdentifier(field.Name))
            {
                result.Errors.Add($"Field '{entityName}.{field.Name}' is not a safe SQL identifier.");
            }

            if (string.IsNullOrWhiteSpace(field.Type))
            {
                result.Errors.Add($"Field '{entityName}.{field.Name}' has no type.");
            }

            // Physical Schema Guard: Check if column exists in the table/view
            if (efEntity != null)
            {
                // First try direct property name match (most common)
                var efProperty = efEntity.FindProperty(field.Name);
                
                if (efProperty == null)
                {
                    // Fallback: Check if any property maps to this column name
                    efProperty = efEntity.GetProperties().FirstOrDefault(p => 
                    {
                        try { return p.GetColumnName().Equals(field.Name, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    });
                }

                if (efProperty == null)
                {
                    var tableName = efEntity.GetTableName() ?? efEntity.GetViewName() ?? entityName;
                    var isView = efEntity.GetViewName() != null;
                    var message = $"Field '{entityName}.{field.Name}' does not exist in the database {(isView ? "view" : "table")} '{tableName}'.";
                    if (isView)
                        result.Warnings.Add(message);
                    else
                        result.Errors.Add(message);
                }
            }

            foreach (var capability in field.Capabilities)
            {
                if (!CopilotDataCatalogSchema.IsSupportedFieldCapability(capability))
                {
                    result.Errors.Add($"Field '{entityName}.{field.Name}' has unsupported capability '{capability}'.");
                }
            }

            foreach (var op in field.Operators)
            {
                if (!CopilotDataCatalogSchema.IsSupportedOperator(op))
                {
                    result.Errors.Add($"Field '{entityName}.{field.Name}' has unsupported operator '{op}'.");
                }
            }

            foreach (var aggregation in field.Aggregations)
            {
                if (!CopilotDataCatalogSchema.IsSupportedAggregation(aggregation))
                {
                    result.Errors.Add($"Field '{entityName}.{field.Name}' has unsupported aggregation '{aggregation}'.");
                }

                if (!field.Capabilities.Contains("aggregate", StringComparer.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"Field '{entityName}.{field.Name}' declares aggregation '{aggregation}' but is not aggregatable.");
                }
            }
        }

        private static void ValidateLookupEnrichment(
            string entityName,
            CopilotLookupEnrichmentDefinition lookupEnrichment,
            HashSet<string> fieldNames,
            CopilotDataCatalogValidationResult result)
        {
            if (!lookupEnrichment.Enabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(lookupEnrichment.ValueField))
            {
                result.Errors.Add($"Entity '{entityName}' lookup enrichment is missing ValueField.");
            }
            else if (!fieldNames.Contains(lookupEnrichment.ValueField))
            {
                result.Errors.Add($"Entity '{entityName}' lookup value field '{lookupEnrichment.ValueField}' is not defined.");
            }

            if (!string.IsNullOrWhiteSpace(lookupEnrichment.ActiveField) &&
                !fieldNames.Contains(lookupEnrichment.ActiveField))
            {
                result.Errors.Add($"Entity '{entityName}' lookup active field '{lookupEnrichment.ActiveField}' is not defined.");
            }

            if (lookupEnrichment.MaxValues <= 0)
            {
                result.Errors.Add($"Entity '{entityName}' lookup enrichment must define MaxValues greater than zero.");
            }
        }

        private static void ValidateRelationship(
            string entityName,
            CopilotRelationshipDefinition relationship,
            HashSet<string> entityNames,
            HashSet<string> fieldNames,
            CopilotDataCatalogValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(relationship.Name))
            {
                result.Errors.Add($"Entity '{entityName}' has a relationship without a name.");
            }
            else if (!CopilotDataCatalogSchema.IsSafeIdentifier(relationship.Name))
            {
                result.Errors.Add($"Relationship '{entityName}.{relationship.Name}' is not a safe identifier.");
            }

            if (!entityNames.Contains(relationship.Target))
            {
                result.Errors.Add($"Relationship '{entityName}.{relationship.Name}' targets unknown entity '{relationship.Target}'.");
            }

            if (relationship.Type.Equals("ManyToMany", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(relationship.Via) ||
                    string.IsNullOrWhiteSpace(relationship.ViaSourceField) ||
                    string.IsNullOrWhiteSpace(relationship.ViaTargetField))
                {
                    result.Errors.Add($"Many-to-many relationship '{entityName}.{relationship.Name}' must define Via, ViaSourceField, and ViaTargetField.");
                }
                else
                {
                    if (!CopilotDataCatalogSchema.IsSafeIdentifier(relationship.Via))
                    {
                        result.Errors.Add($"Relationship '{entityName}.{relationship.Name}' via table '{relationship.Via}' is not a safe SQL identifier.");
                    }

                    if (!CopilotDataCatalogSchema.IsSafeIdentifier(relationship.ViaSourceField))
                    {
                        result.Errors.Add($"Relationship '{entityName}.{relationship.Name}' via source field '{relationship.ViaSourceField}' is not a safe SQL identifier.");
                    }

                    if (!CopilotDataCatalogSchema.IsSafeIdentifier(relationship.ViaTargetField))
                    {
                        result.Errors.Add($"Relationship '{entityName}.{relationship.Name}' via target field '{relationship.ViaTargetField}' is not a safe SQL identifier.");
                    }
                }
            }
            else
            {
                if (!fieldNames.Contains(relationship.SourceField))
                {
                    result.Errors.Add($"Relationship '{entityName}.{relationship.Name}' source field '{relationship.SourceField}' is not defined on '{entityName}'.");
                }
                else if (!CopilotDataCatalogSchema.IsSafeIdentifier(relationship.SourceField))
                {
                    result.Errors.Add($"Relationship '{entityName}.{relationship.Name}' source field '{relationship.SourceField}' is not a safe SQL identifier.");
                }

                if (string.IsNullOrWhiteSpace(relationship.TargetField))
                {
                    result.Errors.Add($"Relationship '{entityName}.{relationship.Name}' is missing TargetField.");
                }
                else if (!CopilotDataCatalogSchema.IsSafeIdentifier(relationship.TargetField))
                {
                    result.Errors.Add($"Relationship '{entityName}.{relationship.Name}' target field '{relationship.TargetField}' is not a safe SQL identifier.");
                }
            }
        }

        private static HashSet<string> BuildAllowedProjectedFields(
            CopilotEntityDefinition entity,
            CopilotDataCatalog catalog,
            HashSet<string> baseFieldNames)
        {
            var allowed = new HashSet<string>(baseFieldNames, StringComparer.OrdinalIgnoreCase);

            foreach (var relationship in entity.Relationships.Where(relationship => relationship.IsDefaultJoin))
            {
                var target = catalog.Entities.FirstOrDefault(candidate =>
                    candidate.Name.Equals(relationship.Target, StringComparison.OrdinalIgnoreCase));
                if (target == null || !TargetHasDisplayProjection(target))
                {
                    continue;
                }

                allowed.Add($"{relationship.Name}Name");
                allowed.Add($"{relationship.Name}Display");

                if (target.Fields.Any(field => field.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)))
                {
                    allowed.Add(relationship.Name);
                }
            }

            return allowed;
        }

        private static bool TargetHasDisplayProjection(CopilotEntityDefinition target)
        {
            var hasName = target.Fields.Any(field => field.Name.Equals("Name", StringComparison.OrdinalIgnoreCase));
            var hasUserDisplay =
                target.Fields.Any(field => field.Name.Equals("FirstName", StringComparison.OrdinalIgnoreCase)) ||
                target.Fields.Any(field => field.Name.Equals("LastName", StringComparison.OrdinalIgnoreCase)) ||
                target.Fields.Any(field => field.Name.Equals("UserName", StringComparison.OrdinalIgnoreCase));

            return hasName || hasUserDisplay;
        }
    }
}
