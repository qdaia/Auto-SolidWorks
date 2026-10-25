using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;
using ModelContextProtocol.Server;

public sealed record DeclaredVisualReview
{
    public required string View { get; init; }
    public required string ImagePath { get; init; }
    public required string ImageSha256 { get; init; }
    public required string NativeSha256 { get; init; }
    public required string Observation { get; init; }
    public bool Passed { get; init; }
}

[McpServerToolType]
public sealed class DeliveryReviewTools
{
    [McpServerTool(Name="cad_review_model_delivery",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("最终/阶段复核：复用同一planId的executionArtifactId保存重开回执，校验文件身份；一次执行源投影列表并登记实际看图后的visualReviews。分别报告原生、声明几何、参数化、源投影、视觉与交付状态。不能用写出图片替代看图；缺证据返回review_required。可能启动SolidWorks读取投影，不重复建模。")]
    public static async Task<object> Review(IModelingExecutor executor,CompactArtifactStore artifacts,string planId,string executionArtifactId,
        SourceProjectionComparisonRequest[]? projections=null,DeclaredVisualReview[]? visualReviews=null,string responseDetail="summary",CancellationToken cancellationToken=default)
    {
        CompactResponses.ValidateDetail(responseDetail);
        var plan=artifacts.LoadPlan(planId);
        var payload=artifacts.LoadResult(executionArtifactId);
        if(payload["pending"]?.GetValue<bool>()==true || payload["outcome_unknown"]?.GetValue<bool>()==true)
            throw new ArgumentException("执行结果未确定；先查询同一request_id，不能接受或重放。");
        var execution=(payload["execution"]??payload).Deserialize<ExecutionResult>(ModelingIrJson.Options)
            ?? throw new JsonException("缺少原生执行回执。");
        if(execution.PlanFingerprint!=ModelingPlanIdentity.Fingerprint(plan) || execution.NativePath!=plan.Output.NativePath || execution.OutcomeUnknown)
            throw new ArgumentException("执行回执与当前计划/输出身份不一致。");
        var currentHash=execution.NativePath is {} path&&File.Exists(path)?DrawingPlanValidation.FileHash(path):null;
        var readback=execution.Evidence.LastOrDefault(e=>e.Stage=="saved_model_readback" && e.Passed);
        var nativePassed=execution.Success && currentHash is not null && readback?.Data is {} data &&
            data.TryGetValue("sha256",out var savedHash) && string.Equals(currentHash,savedHash,StringComparison.OrdinalIgnoreCase);
        var requests=projections??[];var reviews=visualReviews??[];
        if(requests.Length>24 || reviews.Length>6)throw new ArgumentException("一次复核最多24个局部投影和6个视图。");
        if(reviews.Select(r=>r.View).Distinct(StringComparer.Ordinal).Count()!=reviews.Length)
            throw new ArgumentException("视觉复核视图不能重复；保留单一明确结论。");
        var source=plan.DrawingContext;
        foreach(var request in requests)
            if(source is null || request.SourcePath!=source.SourcePath || request.NativePath!=execution.NativePath ||
                !string.Equals(request.Source.SourceSha256,plan.DrawingSourceSha256,StringComparison.OrdinalIgnoreCase) ||
                !source.Views.Any(v=>v.Id==request.Source.ViewId && v.Kind.ToString()==request.Orientation.ToString()) ||
                request.CoveredFeatureIds.Count==0 || request.CoveredFeatureIds.Any(id=>!source.Features.Any(f=>f.Id==id)))
                throw new ArgumentException("源投影必须关联当前图纸、原生文件和已登记特征。");
        var results=new List<JsonNode>();
        // Never spend native capture on a stale/unaccepted saved file.
        if(nativePassed)foreach(var request in requests)
            results.Add(JsonSerializer.SerializeToNode(await SourceProjectionComparisonTools.Compare(executor,artifacts,request,"summary",cancellationToken),ModelingIrJson.Options)!);
        var requiredViews=source?.Views.Where(v=>v.Kind is ModelDrawingView.Front or ModelDrawingView.Top or ModelDrawingView.Left or ModelDrawingView.Right or ModelDrawingView.Bottom or ModelDrawingView.Rear)
            .Select(v=>v.Kind.ToString().ToLowerInvariant()).Append("isometric").Distinct().ToArray()??plan.Output.PreviewViews.ToArray();
        var visualPassed=requiredViews.All(v=>reviews.Any(r=>r.View==v && r.Passed && !string.IsNullOrWhiteSpace(r.Observation) &&
            string.Equals(r.NativeSha256,currentHash,StringComparison.OrdinalIgnoreCase) && File.Exists(r.ImagePath) &&
            string.Equals(DrawingPlanValidation.FileHash(r.ImagePath),r.ImageSha256,StringComparison.OrdinalIgnoreCase) &&
            execution.Evidence.Any(e=>e.Stage=="preview"&&e.Passed&&e.Data is {} preview && preview.GetValueOrDefault("view")==r.View &&
                preview.GetValueOrDefault("path")==r.ImagePath && string.Equals(preview.GetValueOrDefault("sha256"),r.ImageSha256,StringComparison.OrdinalIgnoreCase))));
        var projectionCoverage=source is null || source.Features.Where(f=>f.Critical).All(f=>requests.Any(r=>r.CoveredFeatureIds.Contains(f.Id))) &&
            requiredViews.Where(v=>v!="isometric").All(v=>requests.Any(r=>r.Orientation.ToString().ToLowerInvariant()==v));
        var projectionPassed=source is null || requests.Length>0 && results.Count==requests.Length && results.All(r=>r["success"]?.GetValue<bool>()==true) && projectionCoverage;
        var geometryPassed=execution.GeometryStatus=="passed" && execution.Verification?.Passed==true;
        nativePassed=nativePassed && File.Exists(execution.NativePath) && string.Equals(currentHash,DrawingPlanValidation.FileHash(execution.NativePath!),StringComparison.OrdinalIgnoreCase);
        var accepted=nativePassed && geometryPassed && execution.ParametricStatus=="passed" && visualPassed && reviews.All(r=>r.Passed) && projectionPassed;
        var acceptance=new {native_status=nativePassed?"passed":"failed_or_stale",declared_geometry_status=geometryPassed?"passed":"review_required",
            parametric_status=execution.ParametricStatus,source_projection_status=source is null?"not_applicable":projectionPassed?"declared_scope_passed":"review_required",
            visual_status=visualPassed?"declared_review_passed":"review_required",delivery_status=!accepted?"review_required":execution.IsIntermediateStage?"stage_accepted":"declared_scope_accepted"};
        return CompactResponses.Present(artifacts,new{success=accepted,acceptance,plan_id=planId,execution_artifact_id=executionArtifactId,
            native_sha256=currentHash,required_views=requiredViews,source_projection_coverage_complete=projectionCoverage,projection_results=results,visual_reviews=reviews,
            is_intermediate_stage=execution.IsIntermediateStage,full_drawing_equivalence=false},responseDetail);
    }
}
