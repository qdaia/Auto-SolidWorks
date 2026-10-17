using CadModeling.Ir;
namespace CadModeling.Core;

public static class DrawingStageValidation
{
    public static IReadOnlyList<ModelingDiagnostic> ValidateRecovery(DrawingPlanContext context,ModelingRecoveryOptions recovery,IReadOnlyList<string> operationIds)
    {
        if(context.StageScope is null)return [];
        if(!recovery.Enabled || string.IsNullOrWhiteSpace(recovery.SourceRevisionId) || operationIds.Count==0 ||
            !recovery.AfterOperationIds.Contains(operationIds[^1]))
            return [new("DRAWING_STAGE_CHECKPOINT",DiagnosticSeverity.Error,"阶段需启用恢复、固定源修订，并在最后一个操作后保存检查点。","recovery")];
        return [];
    }
    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context)
    {
        var errors=new List<ModelingDiagnostic>();
        void Error(string message)=>errors.Add(new("DRAWING_STAGE_SCOPE",DiagnosticSeverity.Error,message,"drawing_context.stage_scope"));
        var deferred=context.OmissionReview?.Candidates.Where(c=>c.Disposition==DrawingCandidateDisposition.Deferred).ToArray()??[];
        if(context.StageScope is not { } stage)
        {
            if(deferred.Length>0)Error("最终计划不能保留 Deferred 来源项；完成剩余识图与绑定后再提交。");
            return errors;
        }
        if(!Guid.TryParseExact(stage.SessionId,"N",out _) || string.IsNullOrWhiteSpace(stage.StageId) || stage.Revision<1 ||
            string.IsNullOrWhiteSpace(stage.ProjectionAndUnitsReview) || string.IsNullOrWhiteSpace(stage.TopologyAndDatumReview) ||
            string.IsNullOrWhiteSpace(stage.DependencyClosureReview))
            Error("阶段必须记录会话身份、阶段/修订号和全图投影单位、整体拓扑基准及局部依赖闭合复核。");
        if(!context.RequireCompleteBindings || !context.RequireInterpretationReview || !context.RequireDimensionEvidence || !context.RequireGeometryAnchors)
            Error("分段执行必须启用完整局部绑定、解释、尺寸证据和几何锚点检查。");
        var pending=stage.PendingFeatureIds.ToHashSet(StringComparer.Ordinal);
        if(pending.Count==0 || pending.Count!=stage.PendingFeatureIds.Count || pending.Any(string.IsNullOrWhiteSpace) ||
            context.Features.Any(f=>pending.Contains(f.Id)))
            Error("中间阶段必须明确列出尚未执行且与当前特征不重叠的唯一待办特征；完成时移除 stage_scope。");
        if(context.Features.Count==0 || context.Dimensions.Any(d=>d.Status is DrawingFactStatus.Unknown or DrawingFactStatus.Assumed))
            Error("阶段必须包含已确认特征，不能依赖未知或假设尺寸。");
        foreach(var id in pending)
            if(!deferred.Any(c=>c.DeferredFeatureId==id)) Error("待办特征必须关联原图库存中的延后项："+id);
        foreach(var candidate in deferred)
            if(candidate.DeferredFeatureId is null || !pending.Contains(candidate.DeferredFeatureId))
                Error("延后来源必须关联当前阶段明确的待办特征。");
        return errors;
    }
}
