using CadModeling.Ir;
namespace CadModeling.Core;

public sealed record LocalFaceMeasurement(Vector3 ClosestPointMm, Vector3 OutwardNormal,
    LocalSurfaceKind? Kind, double? DiameterMm=null, double? ConeHalfAngleDegrees=null,
    int? FaceIndex=null, double? AreaMm2=null, double AreaErrorMm2=0,
    double? RadiusMm=null, Vector3? CenterMm=null);
// Complete means every solid face was measured. Missing/failed COM calls must never look like empty space.
public sealed record LocalPointMeasurement(IReadOnlyList<LocalFaceMeasurement> Faces, bool Complete, string? Error=null);

public static partial class ModelVerification
{
    private static void ValidateLocalChecks(ModelVerificationSpec spec,Action<string> error)
    {
        if(spec.SurfaceSamples.Sum(c=>c.PointsMm.Count)+spec.BoundaryClearances.Sum(c=>c.PointsMm.Count)>512)
            error("局部几何检查仅限于每请求声明的512个样本点。");
        foreach(var c in spec.SurfaceSamples)
        {
            if(!Enum.IsDefined(c.SurfaceKind)||c.PointsMm.Count==0||c.PointsMm.Any(p=>!Finite(p))||
                c.OutwardNormals.Count!=c.PointsMm.Count||c.OutwardNormals.Any(n=>!Finite(n)||Norm(n)<1e-12)||
                !Positive(c.ToleranceMm)||!Positive(c.AngleToleranceDegrees)||c.AngleToleranceDegrees>=90)
                error($"曲面样本 '{c.Id}' 需要有限数量的点，对应的非零外法线，并且有效的公差值。");
            if(c.SurfaceKind==LocalSurfaceKind.Cylinder ? c.DiameterMm is not {} d||!Positive(d) : c.DiameterMm is not null)
                error($"曲面样本 '{c.Id}' 只需要圆柱的直径。");
            if(c.SurfaceKind==LocalSurfaceKind.Cone ? c.ConeHalfAngleDegrees is not {} a||!Positive(a)||a>=90 : c.ConeHalfAngleDegrees is not null)
                error($"曲面样本 '{c.Id}' 只需在 (0, 90) 方向上具有半角度，以符合圆锥的要求。");
            if(c.SurfaceKind==LocalSurfaceKind.Sphere ? c.RadiusMm is not {} r||!Positive(r)||c.CenterMm is not {} centre||!Finite(centre)
                : c.RadiusMm is not null||c.CenterMm is not null)
                error($"曲面样本 '{c.Id}' 只需要正数半径和有限中心，才能是球面。");
            if(c.SurfaceKind==LocalSurfaceKind.Sphere && c.CenterMm is {} center && c.RadiusMm is {} radius &&
                c.PointsMm.Any(p=>Math.Abs(Norm(Sub(p,center))-radius)>c.ToleranceMm))
                error($"曲面样本 '{c.Id}' 的探测偏离了预期的球面。");
            if(!Positive(c.AreaToleranceMm2)||c.ExpectedAreaMm2 is {} area&&(!Positive(area)||c.AreaToleranceMm2>=area))
                error($"曲面样本 '{c.Id}' 需要一个正面积和一个较小的正面积容差。");
        }
        foreach(var c in spec.BoundaryClearances)
            if(c.PointsMm.Count==0||c.PointsMm.Any(p=>!Finite(p))||!Positive(c.MinimumDistanceMm)||!Positive(c.ToleranceMm)||c.ToleranceMm>=c.MinimumDistanceMm)
                error($"边界清况 '{c.Id}' 需要有限数量的点，并且与之的最小距离大于其正容差。");
    }

    private static IEnumerable<VerificationCheckResult> EvaluateLocalChecks(ModelVerificationSpec spec,
        IReadOnlyDictionary<string,IReadOnlyList<LocalPointMeasurement>>? measurements)
    {
        VerificationCheckResult Evaluate(string id,IReadOnlyList<Vector3> points,SurfaceSampleCheck? surface,BoundaryClearanceCheck? clearance)
        {
            if(measurements is null||!measurements.TryGetValue(id,out var measured)||measured.Count!=points.Count)
                return new(id,false,"unverifiable","局部修剪面的测量缺失或不完整。");
            var evidence=new Dictionary<string,string>();var failures=new List<string>();bool incomplete=false;
            var matchedAreas=new Dictionary<int,double>();
            var areaErrors=new Dictionary<int,double>();
            for(int i=0;i<points.Count;i++)
            {
                var probe=measured[i];
                if(!probe.Complete||probe.Faces.Count==0||probe.Faces.Any(f=>!Finite(f.ClosestPointMm)))
                {incomplete=true;failures.Add($"点 {i}：{probe.Error??"incomplete boundary measurement"}。");continue;}
                var nearest=probe.Faces.Min(f=>Norm(Sub(f.ClosestPointMm,points[i])));
                evidence[$"point_{i}_mm"]=Point(points[i]);evidence[$"point_{i}_nearest_boundary_mm"]=F(nearest);
                if(clearance is not null)
                {
                    if(nearest+clearance.ToleranceMm<clearance.MinimumDistanceMm) failures.Add($"点{i}：边界距离所需的间隙更近。");
                    continue;
                }
                var check=surface!;var normal=Unit(check.OutwardNormals[i]);
                var candidates=probe.Faces.Where(f=>Norm(Sub(f.ClosestPointMm,points[i]))<=check.ToleranceMm&&f.Kind==check.SurfaceKind).ToArray();
                var valid=candidates.Where(f=>Finite(f.OutwardNormal)&&Norm(f.OutwardNormal)>1e-12&&
                    (check.DiameterMm is null||f.DiameterMm is {} d&&Positive(d))&&
                    (check.RadiusMm is null||f.RadiusMm is {} r&&Positive(r)&&f.CenterMm is {} c&&Finite(c))&&
                    (check.ConeHalfAngleDegrees is null||f.ConeHalfAngleDegrees is {} a&&Positive(a))).ToArray();
                if(valid.Length<candidates.Length) {incomplete=true;failures.Add($"点{i}：无效的原生曲面参数。");continue;}
                evidence[$"point_{i}_candidates"]=string.Join("; ",valid.Select(f=>$"{f.Kind}；法线 {Point(f.OutwardNormal)}；直径 {f.DiameterMm}；半角 {f.ConeHalfAngleDegrees}；半径 {f.RadiusMm}；中心 {(f.CenterMm is {} c?Point(c):"unavailable")}"));
                var matches=valid.Where(f=>Dot(Unit(f.OutwardNormal),normal)+1e-12>=Math.Cos(check.AngleToleranceDegrees*Math.PI/180)&&
                    (check.DiameterMm is null||Math.Abs(f.DiameterMm!.Value-check.DiameterMm.Value)<=check.ToleranceMm)&&
                    (check.RadiusMm is null||Math.Abs(f.RadiusMm!.Value-check.RadiusMm.Value)<=check.ToleranceMm&&Norm(Sub(f.CenterMm!,check.CenterMm!))<=check.ToleranceMm)&&
                    (check.ConeHalfAngleDegrees is null||Math.Abs(f.ConeHalfAngleDegrees!.Value-check.ConeHalfAngleDegrees.Value)<=check.AngleToleranceDegrees)).ToArray();
                if(matches.Length==0)
                    failures.Add($"点{i}：没有实际修剪的{check.SurfaceKind}面匹配位置、外法线和声明的参数。");
                if(check.ExpectedAreaMm2 is not null)
                    foreach(var match in matches)
                    {
                        if(match.FaceIndex is not {} faceId||faceId<0||match.AreaMm2 is not {} area||!Positive(area)||
                            !double.IsFinite(match.AreaErrorMm2)||match.AreaErrorMm2<0||
                            matchedAreas.TryGetValue(faceId,out var prior)&&Math.Abs(prior-area)>1e-8)
                        {incomplete=true;failures.Add($"点{i}：面面积或身份缺失/不一致。");}
                        else {matchedAreas[faceId]=area;areaErrors[faceId]=match.AreaErrorMm2;}
                    }
            }
            if(surface?.ExpectedAreaMm2 is {} expectedArea)
            {
                var actualArea=matchedAreas.Values.Sum();
                var estimatedError=areaErrors.Values.Sum();
                evidence["unique_matched_faces"]=matchedAreas.Count.ToString();
                evidence["measured_area_mm2"]=F(actualArea);evidence["expected_area_mm2"]=F(expectedArea);
                evidence["area_estimated_error_mm2"]=F(estimatedError);
                var difference=Math.Abs(actualArea-expectedArea);
                if(difference-estimatedError>surface.AreaToleranceMm2)failures.Add("样本面的总面积与源面的面积不同。");
                else if(difference+estimatedError>surface.AreaToleranceMm2)
                {incomplete=true;failures.Add("面积测量距离其估计数值精度的边界太近了。");}
            }
            var passed=failures.Count==0;
            evidence["sample_count"]=points.Count.ToString();
            evidence["scope"]=surface is not null?"有限的曲面样本，加上可选的接触面的总面积；并非完整的特征拓扑的精确值。":"距离实体边界仅有；不会分类材料/空腔或证明连通性。";
            return new(id,passed,passed?"passed":incomplete?"unverifiable":"mismatch",passed?"所有声明的本地样本匹配。":string.Join(" ",failures),evidence);
        }
        foreach(var c in spec.SurfaceSamples)yield return Evaluate(c.Id,c.PointsMm,c,null);
        foreach(var c in spec.BoundaryClearances)yield return Evaluate(c.Id,c.PointsMm,null,c);
    }
}
