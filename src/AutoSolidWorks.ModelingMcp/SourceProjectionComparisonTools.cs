using System.ComponentModel;
using System.IO;
using CadModeling.Core;
using CadModeling.Ir;
using ModelContextProtocol.Server;

public sealed record SourceProjectionComparisonRequest
{
    public ProjectionRegionMm? RegionMm { get; init; }
    public IReadOnlyList<string> CoveredFeatureIds { get; init; } = [];
    public required string SourcePath { get; init; }
    public required string NativePath { get; init; }
    public required ProjectionSnapshot Source { get; init; }
    public required string CalibrationEvidence { get; init; }
    public required IReadOnlyDictionary<string,DrawingReviewBox> SourceRegions { get; init; }
    public ProjectionOrientation Orientation { get; init; }=ProjectionOrientation.Front;
    public double OriginXmm { get; init; }
    public double OriginYmm { get; init; }
    public ProjectionComparisonOptions Options { get; init; }=new();
}

[McpServerToolType]
public sealed class SourceProjectionComparisonTools
{
    [McpServerTool(Name="cad_compare_source_projection",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("将已复核原图的线/圆/弧毫米坐标与保存模型的实际投影比较，返回差异及原图定位。先从标注和基准确定坐标，禁止由模型回填期望或自动缩放拟合。可能启动 SolidWorks；不认证整件图纸等价。合同 source_projection。")]
    public static async Task<object> Compare(IModelingExecutor executor,CompactArtifactStore artifacts,SourceProjectionComparisonRequest request,
        string responseDetail="summary",CancellationToken cancellationToken=default)
    {
        CompactResponses.ValidateDetail(responseDetail);
        ProjectionVerifier.ValidateSourceInput(request.Source,request.Options);
        if(!Path.IsPathFullyQualified(request.SourcePath) || !File.Exists(request.SourcePath) ||
            !Path.IsPathFullyQualified(request.NativePath) || !File.Exists(request.NativePath) ||
            !string.Equals(DrawingPlanValidation.FileHash(request.SourcePath),request.Source.SourceSha256,StringComparison.OrdinalIgnoreCase) ||
            request.Source.NativeModelPath is not null || request.Source.NativeModelSha256 is not null || request.Source.NativeModelReopened ||
            string.IsNullOrWhiteSpace(request.CalibrationEvidence) || !Enum.IsDefined(request.Orientation) ||
            !double.IsFinite(request.OriginXmm) || !double.IsFinite(request.OriginYmm) ||
            !request.Source.CaptureComplete || request.Source.CaptureLimitations.Count>0 || request.Source.Primitives.Count is <1 or >10000 ||
            request.Source.Primitives.Any(p=>!request.SourceRegions.TryGetValue(p.Id,out var box) || !DrawingOmissionValidation.ValidBox(box)))
            throw new ArgumentException("源投影需要当前原图哈希、独立的完整线/圆/弧、原图区域和固定毫米基准；不能采用模型测量充当来源。");
        // Preserve the independent expectation before native capture is requested.
        var source=request.RegionMm is {} local?ProjectionLocalScope.Select(request.Source,local):request.Source;
        if(source.Primitives.Count==0)throw new ArgumentException("局部区域没有必需源图元，不能提交空范围验收。");
        var expectation=artifacts.SaveResult(request);
        var before=DrawingPlanValidation.FileHash(request.NativePath);
        var capture=await executor.CaptureProjectionAsync(new(){NativePath=request.NativePath,SourceDrawingSha256=request.Source.SourceSha256,
            ViewId=request.Source.ViewId,CoordinateFrameId=request.Source.CoordinateFrameId,Orientation=request.Orientation,
            OriginXmm=request.OriginXmm,OriginYmm=request.OriginYmm},cancellationToken);
        if(!capture.Success || capture.Snapshot is null || !capture.Complete)
            return CompactResponses.Present(artifacts,new{success=false,status="capture_unverifiable",capture,expectation_id=expectation.ArtifactId,
                full_drawing_equivalence=false},responseDetail);
        if(!string.Equals(before,DrawingPlanValidation.FileHash(request.NativePath),StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before,capture.Snapshot.NativeModelSha256,StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(DrawingPlanValidation.FileHash(request.SourcePath),request.Source.SourceSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("比较期间原图或原生模型身份发生变化，结果不可复用。");
        var actual=capture.Snapshot;
        if(request.RegionMm is {} region)
        {
            actual=ProjectionLocalScope.Select(actual,region);
        }
        var report=ProjectionVerifier.Compare(source,actual,request.Options);
        var locations=report.Differences.Select(d=>new{difference=d,
            source_bounds=d.SourcePrimitiveId is { } id && request.SourceRegions.TryGetValue(id,out var bounds)?bounds:null}).ToArray();
        return CompactResponses.Present(artifacts,new{success=report.Passed,status=report.Passed?"declared_projection_passed":"projection_mismatch",
            difference_count=report.Differences.Count,expectation_id=expectation.ArtifactId,report,locations,capture,
            local_region_mm=request.RegionMm,covered_feature_ids=request.CoveredFeatureIds,
            scope=request.RegionMm is null?"declared_full_view_primitives":"complete_primitives_with_envelopes_intersecting_declared_region",
            full_drawing_equivalence=false},responseDetail);
    }
}
