using System.Text.Json;
using System.Text.Json.Nodes;

namespace CadModeling.Drawing.Contracts;

public sealed record SchemaMigrationResult(string? Json, IReadOnlyList<ContractDiagnostic> Diagnostics)
{
    public bool Success => Json is not null && Diagnostics.All(item => item.Severity != ContractDiagnosticSeverity.Error);
}

public sealed class SchemaMigrator
{
    public SchemaMigrationResult Migrate(string json, string targetVersion, DateTimeOffset migratedAt)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject()
                ?? throw new JsonException("Migration input is empty.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Failed("MIG001", exception.Message, string.Empty, "$");
        }

        var documentId = root["document_id"]?.GetValue<string>() ?? string.Empty;
        var sourceVersion = root["schema_version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sourceVersion))
            return Failed("MIG002", "schema_version is required; old documents are never interpreted using current semantics silently.", documentId, "schema_version");
        if (targetVersion != DrawingContractSchema.CurrentVersion)
            return Failed("MIG003", $"Unsupported migration target '{targetVersion}'.", documentId, "schema_version");
        if (sourceVersion == targetVersion)
            return new(DrawingContractJson.SerializeDeterministic(root), []);
        if (sourceVersion != DrawingContractSchema.LegacyVersion)
            return Failed("MIG004", $"No explicit migration is registered for '{sourceVersion}' -> '{targetVersion}'.", documentId, "schema_version");

        if (root["source_hash"] is null)
            return Failed("MIG005", "Legacy 0.9.0 document requires source_hash for the explicit migration.", documentId, "source_hash");

        root["source_sha256"] = root["source_hash"]!.DeepClone();
        root.Remove("source_hash");
        root["schema_version"] = targetVersion;
        var history = root["migration_history"] as JsonArray ?? [];
        history.Add(new JsonObject
        {
            ["from_version"] = sourceVersion,
            ["to_version"] = targetVersion,
            ["migrator_name"] = "drawing-contracts-0.9.0-to-1.0.0",
            ["migrated_at"] = migratedAt,
            ["rationale"] = "Explicitly rename source_hash to source_sha256; no fact or evidence status is changed."
        });
        root["migration_history"] = history;
        return new(DrawingContractJson.SerializeDeterministic(root), []);
    }

    private static SchemaMigrationResult Failed(string code, string message, string documentId, string path) =>
        new(null,
        [
            new ContractDiagnostic
            {
                Id = $"{documentId}:{code}",
                Code = code,
                Severity = ContractDiagnosticSeverity.Error,
                Blocking = true,
                Message = message,
                DocumentId = documentId,
                FieldPath = path
            }
        ]);
}
