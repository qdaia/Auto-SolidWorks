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
            SourcePath = inventory.SourcePath, RequireCompleteBindings = true, RequireInterpretationReview = true, RequireDimensionEvidence = true,
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
            recognition_checklist = RecognitionChecklist, raw_regions = inventory.Regions, worklists
        });
        return new
        {
            status = "pending_independent_visual_review", compiled = false, full_drawing_equivalence = false,
            drawing_context_input_id = contextReceipt.ArtifactId, drawing_context_input_sha256 = contextReceipt.Sha256,
            worklist_artifact_id = workspace.ArtifactId, worklist_artifact_sha256 = workspace.Sha256,
            candidate_count = inventory.Candidates.Count, region_count = inventory.Regions.Count,
            source_revision_id = sourceRevisionId,
            dimension_binding_revision = DimensionEvidenceValidation.Revision,
            recognition_checklist = RecognitionChecklist,
            message = "全部候选与原始区域已整理；用 cad_read_result 分页读取工作清单，实际看图后增量填写上下文。尚无候选或区域被判为已审。"
        };
    }

    public static readonly string[] RecognitionChecklist =
    [
        "先看整页确认视图、剖视/局部放大、单位、比例及技术要求；无标识且无冲突时按第一角，投影符号/视图标签优先。",
        "逐区域建立全尺寸清单，保留原文、R/直径符号、数量、螺距、角度、深度、公差与配合；旋转文字和密集标注须放大复读。禁止按像素比例猜尺寸。",
        "沿尺寸线、界线或引线绑定对象，线性/角度分别记录起止基准。相邻、同值和看起来合理不能证明同一对象。",
        "每个关键尺寸填写 binding_evidence：feature_id、measurement_id、view_id、quantity_kind、原图归一化 text_bounds/target_bounds、datum、attachment_explanation；线性/角度另填 start_reference/end_reference。",
        "同一物理测量跨视图使用同一 measurement_id；不同物理量不得因数值相同合并。算术派生量用 Derived、derived_from_dimension_ids 和 derivation；纯几何基准用 derivation_kind=Geometry 并引用库存中的几何 observation_ids，半径/直径换算显式记录。",
        "交叉核对总长与分段、孔径与外圆、中心距与边距、通孔与盲孔、实体与隐藏线；补查无文字候选区域。最后从原图反查每个标注是否入账，冲突集中返回原图复核。"
    ];
}
