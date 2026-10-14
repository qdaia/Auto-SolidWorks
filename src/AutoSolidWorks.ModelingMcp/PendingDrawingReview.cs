using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using CadModeling.Core;
using CadModeling.Ir;

/// <summary>Deterministic pending inputs only; no source interpretation or acceptance.</summary>
public static class PendingDrawingReview
{
    public static object Prepare(CompactArtifactStore store, string inventoryPath, string inventorySha256,
        string? sourceRevisionId = null, string? expectedSourcePath = null, string? expectedSourceSha256 = null)
    {
        if (inventorySha256.Length != 64 || !inventorySha256.All(Uri.IsHexDigit))
            throw new ArgumentException("必须使用读取图纸回执中的完整库存 SHA-256。");
        var inventory = DrawingOmissionValidation.Load(inventoryPath, inventorySha256);
        if (!inventory.CompleteExtraction || inventory.Pages.Any(p => !p.Ingested))
            throw new InvalidDataException("源页提取不完整，无法建立完整待审工作清单。");
        if (expectedSourcePath is not null && !string.Equals(Path.GetFullPath(expectedSourcePath),
                Path.GetFullPath(inventory.SourcePath), StringComparison.OrdinalIgnoreCase) ||
            expectedSourceSha256 is not null && !string.Equals(expectedSourceSha256, inventory.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("读取图纸回执与库存的来源身份不一致。");
        var context = new DrawingPlanContext
        {
            SourcePath = inventory.SourcePath, RequireCompleteBindings = true, RequireInterpretationReview = true,
            OmissionReview = new()
            {
                InventoryPath = Path.GetFullPath(inventoryPath), InventorySha256 = inventorySha256.ToLowerInvariant(),
                Regions = inventory.Regions.Select(r => new DrawingRegionReview { RegionId = r.Id, State = DrawingReviewState.Unresolved }).ToArray()
            }
        };
        var regions = inventory.Regions.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var grouped = inventory.Candidates.GroupBy(c => c.ReviewRegionIds.FirstOrDefault(id => regions[id].Purpose != "overview")
            ?? c.ReviewRegionIds[0]).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var worklists = inventory.Regions.Select(r => new
        {
            region_id = r.Id, candidates = grouped.GetValueOrDefault(r.Id) ?? []
        }).ToArray();
        // Validate every external dependency before writing either immutable artifact.
        var contextReceipt = store.SaveInput("context", JsonSerializer.SerializeToNode(context, ModelingIrJson.Options)!, null, context, null);
        var workspace = store.SaveResult(new
        {
            source_path = inventory.SourcePath, source_sha256 = inventory.SourceSha256, source_revision_id = sourceRevisionId,
            inventory_path = Path.GetFullPath(inventoryPath), inventory_sha256 = inventorySha256.ToLowerInvariant(),
            status = "pending_independent_visual_review", semantic_review_complete = false,
            raw_regions = inventory.Regions, worklists
        });
        return new
        {
            status = "pending_independent_visual_review", compiled = false, full_drawing_equivalence = false,
            drawing_context_input_id = contextReceipt.ArtifactId, drawing_context_input_sha256 = contextReceipt.Sha256,
            worklist_artifact_id = workspace.ArtifactId, worklist_artifact_sha256 = workspace.Sha256,
            candidate_count = inventory.Candidates.Count, region_count = inventory.Regions.Count,
            source_revision_id = sourceRevisionId,
            message = "全部候选与原始区域已整理；用 cad_read_result 分页读取工作清单，实际看图后增量填写上下文。尚无候选或区域被判为已审。"
        };
    }
}
