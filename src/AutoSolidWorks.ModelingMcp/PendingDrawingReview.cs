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
            SourcePath=inventory.SourcePath, WorkflowRevision=SourceWorkflow.DefaultRevision,
            RequireCompleteBindings=true, RequireDimensionEvidence=true, RequireGeometryAnchors=true,
            RequireInterpretationReview=false, RequireShapeDecomposition=false,
            SourceEvidence=new(){InventoryPath=Path.GetFullPath(inventoryPath),InventorySha256=inventorySha256.ToLowerInvariant()}
        };
        var contextReceipt=store.SaveInput("context",JsonSerializer.SerializeToNode(context,ModelingIrJson.Options)!,null,context,null);
        var workspace=store.SaveResult(new {
            source_path=inventory.SourcePath,source_sha256=inventory.SourceSha256,source_revision_id=sourceRevisionId,
            inventory_path=Path.GetFullPath(inventoryPath),inventory_sha256=inventorySha256.ToLowerInvariant(),
            status="pending_recognition_and_binding",semantic_review_complete=false,
            recognition_checklist=RecognitionChecklist,raw_regions=inventory.Regions,
            workflow_steps=new[]{"recognition","dimension_binding","compile","model"},
            candidate_disposition_required=false,annotation_sweeps_required=false,shape_decomposition_required=false
        });
        return new {
            status="pending_recognition_and_binding",compiled=false,full_drawing_equivalence=false,
            drawing_context_input_id=contextReceipt.ArtifactId,drawing_context_input_sha256=contextReceipt.Sha256,
            worklist_artifact_id=workspace.ArtifactId,worklist_artifact_sha256=workspace.Sha256,
            candidate_count=inventory.Candidates.Count,region_count=inventory.Regions.Count,source_revision_id=sourceRevisionId,
            dimension_binding_revision=DimensionEvidenceValidation.Revision,workflow_revision=SourceWorkflow.DefaultRevision,
            workflow_steps=new[]{"recognition","dimension_binding","compile","model"},
            recognition_checklist=RecognitionChecklist,
            message="原图与绑定证据已准备。识图后直接填写视图、特征、尺寸及参数绑定，编译通过即建模；无需候选核销、双遍扫描、分区探针或独立交付审核。"
        };
    }
    public static readonly string[] RecognitionChecklist=
    [
        "识图：查看原图，确定单位、视图、实体/孔槽和方向；无标识且无冲突时默认第一角，图示标签与用户修正优先。必要时放大密集细节，不进行OCR或按像素比例猜尺寸。",
        "尺寸绑定：保留标注原文，沿界线/引线确定对象、基准和端点，绑定真实operation_id/parameter_path。复合标注逐项绑定数量、孔径、深度，保留其他原文；同值不代表同一对象。",
        "编译：一次提交草案，检查真实参数、单位、表达式、源图端点和操作合法性；有错修正当前错误再编译，不编造证据或未知尺寸。",
        "建模：用plan_id直接调用cad_build_model，等待完成并返回原生文件和简短结果。无需额外审批、健康检查、独立投影、双遍复核或交付报告。"
    ];
}
