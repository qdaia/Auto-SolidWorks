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
                ?? throw new JsonException("迁移输入为空。");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Failed("MIG001", exception.Message, string.Empty, "$");
        }

        var documentId = root["document_id"]?.GetValue<string>() ?? string.Empty;
        var sourceVersion = root["schema_version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sourceVersion))
            return Failed("MIG002", "schema_version 是必需的；旧文档不会静默地使用当前语义进行解释。", documentId, "schema_version");
        if (targetVersion != DrawingContractSchema.CurrentVersion)
            return Failed("MIG003", $"不支持的目标迁移 '{targetVersion}'。", documentId, "schema_version");
        if (sourceVersion == targetVersion)
            return new(DrawingContractJson.SerializeDeterministic(root), []);
        if (sourceVersion != DrawingContractSchema.LegacyVersion)
            return Failed("MIG004", $"没有为 '{sourceVersion}' -> '{targetVersion}' 注册显式的迁移。", documentId, "schema_version");

        if (root["source_hash"] is null)
            return Failed("MIG005", "legacy 0.9.0 文档需要 source_hash 进行明确的迁移。", documentId, "source_hash");

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
            ["rationale"] = "显式地将source_hash重命名为source_sha256；事实或证据的状态没有改变。"
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
