using CadModeling.Ir;
using System.Globalization;
namespace CadModeling.Core;
public sealed record ShapeMeasurement(bool Complete, double? LengthMm=null, Vector3? StartPointMm=null, Vector3? EndPointMm=null,
    double? MaximumGapMm=null, double? MaximumAngleDegrees=null, int Samples=0, string? Error=null,
    double? MaximumNormalCurvatureDifferencePerMm=null,int CurvatureSamples=0);
public static partial class ModelVerification
{
    private static void ValidateShapeChecks(ModelVerificationSpec spec, Action<string> error)
    {
        foreach(var c in spec.EdgeShapes)
            if(c.Edge.Kind!=EntityKind.Edge || c.Edge.AllMatches || !Positive(c.LengthMm) || !Positive(c.ToleranceMm) || !Finite(c.StartPointMm) || !Finite(c.EndPointMm))
                error($"边 '{c.Id}' 的形状需要一个边、正长度/容差和有限的端点。");
        foreach(var c in spec.SurfaceContinuity)
            if(c.Edge.Kind!=EntityKind.Edge || c.Edge.AllMatches || c.Samples is <3 or >4096 || !Positive(c.GapToleranceMm)
                || !double.IsFinite(c.AngleToleranceDegrees) || c.AngleToleranceDegrees<0 || c.AngleToleranceDegrees>=90
                || !Positive(c.CurvatureTolerancePerMm) || c.RequireCurvatureContinuity&&!c.RequireTangency)
                error($"连续性 '{c.Id}' 需要一个缝合边，3..4096 样本，正的间隙和曲率公差及 [0, 90) 角度；G2 要求同时检查切平面。");
        var queries=spec.EdgeShapes.Select(c=>c.Edge).Concat(spec.SurfaceContinuity.Select(c=>c.Edge)).ToArray();
        var diagnostics=new List<ModelingDiagnostic>();
        AdvancedFeatureValidation.Validate(new(){Selections=queries},"verification",diagnostics);
        foreach(var d in diagnostics)error(d.Message);
    }
    private static IEnumerable<VerificationCheckResult> EvaluateShapeChecks(ModelVerificationSpec spec, IReadOnlyDictionary<string,ShapeMeasurement>? measured)
    {
        foreach(var c in spec.EdgeShapes)
        {
            var m=measured?.GetValueOrDefault(c.Id);
            if(m is not {Complete:true,LengthMm:not null,StartPointMm:not null,EndPointMm:not null} || !double.IsFinite(m.LengthMm.Value) || !Finite(m.StartPointMm) || !Finite(m.EndPointMm))
            {yield return new(c.Id,false,"unverifiable",m?.Error??"草图中的边线几何缺失。");continue;}
            var gap=Math.Min(Math.Max(Norm(Sub(m.StartPointMm,c.StartPointMm)),Norm(Sub(m.EndPointMm,c.EndPointMm))),Math.Max(Norm(Sub(m.EndPointMm,c.StartPointMm)),Norm(Sub(m.StartPointMm,c.EndPointMm))));
            var ok=Math.Abs(m.LengthMm.Value-c.LengthMm)<=c.ToleranceMm && gap<=c.ToleranceMm;
            yield return new(c.Id,ok,ok?"passed":"mismatch","原生修剪边的长度和端点被检查。",new Dictionary<string,string>{["length_mm"]=F(m.LengthMm.Value),["endpoint_error_mm"]=F(gap)});
        }
        foreach(var c in spec.SurfaceContinuity)
        {
            var m=measured?.GetValueOrDefault(c.Id);
            if(m is not {Complete:true,MaximumGapMm:not null,MaximumAngleDegrees:not null} || m.Samples!=c.Samples || !double.IsFinite(m.MaximumGapMm.Value) || !double.IsFinite(m.MaximumAngleDegrees.Value)
                || m.MaximumGapMm.Value<0 || m.MaximumAngleDegrees.Value<0 || m.MaximumAngleDegrees.Value>90
                || c.RequireCurvatureContinuity && (m.CurvatureSamples!=c.Samples || m.MaximumNormalCurvatureDifferencePerMm is not { } curvature || !double.IsFinite(curvature) || curvature<0))
            {yield return new(c.Id,false,"unverifiable",m?.Error??"完成两个面缝合测量缺失。");continue;}
            var ok=m.MaximumGapMm.Value<=c.GapToleranceMm && (!c.RequireTangency || m.MaximumAngleDegrees.Value<=c.AngleToleranceDegrees)
                && (!c.RequireCurvatureContinuity || m.MaximumNormalCurvatureDifferencePerMm!.Value<=c.CurvatureTolerancePerMm);
            var measurements=new Dictionary<string,string>{["max_gap_mm"]=F(m.MaximumGapMm.Value),["max_angle_degrees"]=F(m.MaximumAngleDegrees.Value),["samples"]=m.Samples.ToString(CultureInfo.InvariantCulture),["scope"]="sampled_native_seam"};
            if(c.RequireCurvatureContinuity)
            {
                measurements["max_normal_curvature_difference_per_mm"]=F(m.MaximumNormalCurvatureDifferencePerMm!.Value);
                measurements["curvature_samples"]=m.CurvatureSamples.ToString(CultureInfo.InvariantCulture);
                measurements["continuity_order"]="G2";
            }
            yield return new(c.Id,ok,ok?"passed":"mismatch",c.RequireCurvatureContinuity
                ?"沿原生接缝采样检查位置、切平面及共同切平面内所有方向的法曲率差；未采样区域未验证。"
                :"沿原生接缝采样检查位置及切平面夹角；G2 曲率和未采样区域未验证。",measurements);
        }
    }
}
