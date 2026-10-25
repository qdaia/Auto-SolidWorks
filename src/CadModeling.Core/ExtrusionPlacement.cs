using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Independent source expectation along a declared global axis; millimetres from DatumPointMm.</summary>
public sealed record ExtrusionPlacementIntent
{
    public required Vector3 AxisDirection { get; init; }
    public required Vector3 DatumPointMm { get; init; }
    public required double MinimumMm { get; init; }
    public required double MaximumMm { get; init; }
    public double ToleranceMm { get; init; } = .01;
    public required string SourceExplanation { get; init; }
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
}

public static class ExtrusionPlacement
{
    public static IReadOnlyList<ModelingDiagnostic> Validate(GenericModelDraft draft)
    {
        var result=new List<ModelingDiagnostic>();
        foreach(var op in draft.Operations.Where(o=>o.Type is GenericOperationKind.ExtrudeBoss or GenericOperationKind.ExtrudeCut))
        {
            var finite=op.EndCondition is ExtrudeEndCondition.Blind or ExtrudeEndCondition.MidPlane;
            var intent=op.PlacementIntent;
            var required=finite && SourceWorkflow.IsGoverned(draft.DrawingContext?.WorkflowRevision);
            void Error(string code,string message)=>result.Add(new(code,DiagnosticSeverity.Error,message,"operations."+op.Id+".placement_intent"));
            if(intent is null)
            {
                if(required) Error("EXTRUDE_SOURCE_PLACEMENT_REQUIRED","提供源图独立的厚度区间、轴向和基准，先核对单侧/居中；不能用当前拉伸参数回填期望。");
                continue;
            }
            if(!finite || !ModelVerification.Finite(intent.AxisDirection) || ModelVerification.Norm(intent.AxisDirection)<1e-12 ||
                !ModelVerification.Finite(intent.DatumPointMm) || !double.IsFinite(intent.MinimumMm) || !double.IsFinite(intent.MaximumMm) || intent.MaximumMm<=intent.MinimumMm ||
                !double.IsFinite(intent.ToleranceMm) || intent.ToleranceMm<0 || intent.ToleranceMm>.1 || string.IsNullOrWhiteSpace(intent.SourceExplanation))
            { Error("EXTRUDE_SOURCE_PLACEMENT_INVALID","厚度区间需要有限有序边界、非零轴、基准、0..0.1 mm公差及来源说明，只用于Blind/MidPlane。");continue; }
            if(required)
            {
                var sourceObservations=draft.DrawingContext!.Dimensions.SelectMany(d=>d.ObservationIds).ToHashSet(StringComparer.Ordinal);
                if(intent.ObservationIds.Count==0 || intent.ObservationIds.Any(id=>!sourceObservations.Contains(id)))
                    Error("EXTRUDE_SOURCE_PLACEMENT_EVIDENCE","厚度定位依据必须引用已登记的源尺寸观察，不能只写建模参数。");
            }
            var sketch=draft.Operations.FirstOrDefault(o=>o.Id==op.SketchId);
            var frame=sketch is {FaceAttachment:null,PlaneId:null}?sketch.Frame??Frame(sketch.Plane??ReferencePlane.Front):null;
            if(frame is null || !ModelVerification.Finite(frame.Normal) || ModelVerification.Norm(frame.Normal)<1e-12 || op.DepthMm is not >0)
            { Error("EXTRUDE_SOURCE_PLACEMENT_UNRESOLVED","无法离线确定源区间；显式提供可解析的草图基准后再执行依赖操作。");continue; }
            var n=ModelVerification.Unit(frame.Normal);var axis=ModelVerification.Unit(intent.AxisDirection);
            var alignment=ModelVerification.Dot(n,axis);
            if(Math.Abs(alignment)<1-1e-8){Error("EXTRUDE_SOURCE_AXIS_MISMATCH","源厚度轴与草图法向不平行。");continue;}
            var centre=ModelVerification.Dot(ModelVerification.Sub(frame.OriginMm,intent.DatumPointMm),axis)+alignment*op.StartOffsetMm*(op.ReverseStartOffset?-1:1);
            var reach=alignment*op.DepthMm.Value*(op.ReverseDirection?-1:1);
            var lo=op.EndCondition==ExtrudeEndCondition.MidPlane?centre-Math.Abs(reach)/2:Math.Min(centre,centre+reach);
            var hi=op.EndCondition==ExtrudeEndCondition.MidPlane?centre+Math.Abs(reach)/2:Math.Max(centre,centre+reach);
            var mismatch=Math.Abs(lo-intent.MinimumMm)>intent.ToleranceMm || Math.Abs(hi-intent.MaximumMm)>intent.ToleranceMm;
            result.Add(new(mismatch?"EXTRUDE_SOURCE_PLACEMENT_MISMATCH":"EXTRUDE_SOURCE_PLACEMENT_CHECKED",mismatch?DiagnosticSeverity.Error:DiagnosticSeverity.Info,
                FormattableString.Invariant($"源厚度区间[{intent.MinimumMm:G12},{intent.MaximumMm:G12}] mm；计划区间[{lo:G12},{hi:G12}] mm。仅检查声明的位置，连接和最终形状仍需原生与源投影验收。"),"operations."+op.Id+".placement_intent"));
        }
        return result;
    }
    private static SketchFrame Frame(ReferencePlane plane)=>new(){Normal=plane switch{ReferencePlane.Top=>new(0,1,0),ReferencePlane.Right=>new(1,0,0),_=>new(0,0,1)},XDirection=plane==ReferencePlane.Right?new(0,0,-1):new(1,0,0)};
}
