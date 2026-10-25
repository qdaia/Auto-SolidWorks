using CadModeling.Ir;
namespace CadModeling.Core;

public static class SourceWorkflow
{
    public const string Revision="source-workflow-5";
    public const string FourStageRevision="source-four-stage-1";
    public const string OrientedFourStageRevision="source-four-stage-2";
    public const string DefaultRevision=OrientedFourStageRevision;
    public const string SourceReviewRevision="source-workflow-4";
    public const string DecompositionRevision="source-workflow-3";
    public const string LegacyRevision="source-workflow-2";
    public static bool IsFourStage(string? revision) => revision is FourStageRevision or OrientedFourStageRevision;
    public static bool IsGoverned(string? revision) => revision is FourStageRevision or OrientedFourStageRevision or Revision or SourceReviewRevision or DecompositionRevision or LegacyRevision;
    public static bool RequiresAnnotationLedger(string? revision) => revision == Revision;
    public static bool UsesAnnotationComponents(string? revision) => revision is FourStageRevision or OrientedFourStageRevision or Revision;
    public static bool RequiresSourceReview(string? revision) => revision is FourStageRevision or OrientedFourStageRevision or Revision or SourceReviewRevision;
    public static bool RequiresDecomposition(string? revision) => revision is Revision or SourceReviewRevision or DecompositionRevision;
    public static DrawingSourceEvidence? Evidence(DrawingPlanContext context) => (IsFourStage(context.WorkflowRevision)?context.SourceEvidence:null) ??
        (context.OmissionReview is {} old ? new(){InventoryPath=old.InventoryPath,InventorySha256=old.InventorySha256}:null);
    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context)
    {
        if(context.WorkflowRevision is null)return []; // Previously compiled plans retain their identity.
        var result=new List<ModelingDiagnostic>();
        void Error(string code,string message,string path)=>result.Add(new(code,DiagnosticSeverity.Error,message,path));
        if(!IsGoverned(context.WorkflowRevision))Error("SOURCE_WORKFLOW_VERSION","未知的图纸工作流修订。","drawing_context.workflow_revision");
        if(RequiresDecomposition(context.WorkflowRevision) && !context.RequireShapeDecomposition)
            Error("SOURCE_WORKFLOW_GATES","新图纸任务必须启用形状分解；先拆出材料/空缺区，再绑定操作。","drawing_context.require_shape_decomposition");
        if(!context.RequireCompleteBindings || !context.RequireDimensionEvidence || !context.RequireGeometryAnchors ||
            !IsFourStage(context.WorkflowRevision) && !context.RequireInterpretationReview)
            Error("SOURCE_WORKFLOW_GATES","新图纸任务必须启用完整绑定、来源解释、尺寸证据及几何锚点检查。","drawing_context");
        if(IsFourStage(context.WorkflowRevision) && Evidence(context) is null)
            Error("SOURCE_BINDING_PIXELS_REQUIRED","尺寸绑定需要 cad_read_drawing 返回的源图证据引用；无需候选核销或双遍扫描。","drawing_context.source_evidence");
        foreach(var f in context.Dimensions)
        {
            if(f.ParameterPath.StartsWith("placement_intent.",StringComparison.Ordinal))
                Error("SOURCE_BINDING_EXPECTATION_ONLY","源尺寸须绑定实际建模参数，不能仅绑定预期厚度区间。","drawing_context.dimensions."+f.Id);
            if(f.Status==DrawingFactStatus.Derived && f.DerivationKind==DrawingDerivationKind.Arithmetic && f.Expression is null)
                Error("SOURCE_FORMULA_REQUIRED","算术派生尺寸必须提供可计算 expression，不能另存手算结果。","drawing_context.dimensions."+f.Id);
            if(f.BindingEvidence is { QuantityKind: DrawingQuantityKind.Linear or DrawingQuantityKind.Angle } e &&
                (Generic(e.StartReference) || Generic(e.EndReference)))
                Error("SOURCE_ENDPOINT_SPECIFIC","说明具体被测端点/轴线/面及基准；不能沿用通用起止界线占位语。","drawing_context.dimensions."+f.Id);
        }
        return result;
    }
    private static bool Generic(string? value)=>value?.Trim() is "尺寸起始界线或角度基准射线" or "尺寸终止界线或被测射线" or "start" or "end";
}
