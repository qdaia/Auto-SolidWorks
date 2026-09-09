using CadModeling.Ir;
namespace CadModeling.Core;

public sealed record LocalFaceMeasurement(Vector3 ClosestPointMm, Vector3 OutwardNormal,
    LocalSurfaceKind? Kind, double? DiameterMm=null, double? ConeHalfAngleDegrees=null,
    int? FaceIndex=null, double? AreaMm2=null, double AreaErrorMm2=0);
// Complete means every solid face was measured. Missing/failed COM calls must never look like empty space.
public sealed record LocalPointMeasurement(IReadOnlyList<LocalFaceMeasurement> Faces, bool Complete, string? Error=null);

public static partial class ModelVerification
{
    private static void ValidateLocalChecks(ModelVerificationSpec spec,Action<string> error)
    {
        if(spec.SurfaceSamples.Sum(c=>c.PointsMm.Count)+spec.BoundaryClearances.Sum(c=>c.PointsMm.Count)>512)
            error("Local geometry inspection is limited to 512 declared sample points per request.");
        foreach(var c in spec.SurfaceSamples)
        {
            if(!Enum.IsDefined(c.SurfaceKind)||c.PointsMm.Count==0||c.PointsMm.Any(p=>!Finite(p))||
                c.OutwardNormals.Count!=c.PointsMm.Count||c.OutwardNormals.Any(n=>!Finite(n)||Norm(n)<1e-12)||
                !Positive(c.ToleranceMm)||!Positive(c.AngleToleranceDegrees)||c.AngleToleranceDegrees>=90)
                error($"Surface sample '{c.Id}' requires finite points, corresponding nonzero outward normals and valid tolerances.");
            if(c.SurfaceKind==LocalSurfaceKind.Cylinder ? c.DiameterMm is not {} d||!Positive(d) : c.DiameterMm is not null)
                error($"Surface sample '{c.Id}' requires diameter only for a cylinder.");
            if(c.SurfaceKind==LocalSurfaceKind.Cone ? c.ConeHalfAngleDegrees is not {} a||!Positive(a)||a>=90 : c.ConeHalfAngleDegrees is not null)
                error($"Surface sample '{c.Id}' requires a half angle in (0,90) only for a cone.");
            if(!Positive(c.AreaToleranceMm2)||c.ExpectedAreaMm2 is {} area&&(!Positive(area)||c.AreaToleranceMm2>=area))
                error($"Surface sample '{c.Id}' requires a positive area and a smaller positive area tolerance.");
        }
        foreach(var c in spec.BoundaryClearances)
            if(c.PointsMm.Count==0||c.PointsMm.Any(p=>!Finite(p))||!Positive(c.MinimumDistanceMm)||!Positive(c.ToleranceMm)||c.ToleranceMm>=c.MinimumDistanceMm)
                error($"Boundary clearance '{c.Id}' requires finite points and a minimum distance larger than its positive tolerance.");
    }

    private static IEnumerable<VerificationCheckResult> EvaluateLocalChecks(ModelVerificationSpec spec,
        IReadOnlyDictionary<string,IReadOnlyList<LocalPointMeasurement>>? measurements)
    {
        VerificationCheckResult Evaluate(string id,IReadOnlyList<Vector3> points,SurfaceSampleCheck? surface,BoundaryClearanceCheck? clearance)
        {
            if(measurements is null||!measurements.TryGetValue(id,out var measured)||measured.Count!=points.Count)
                return new(id,false,"unverifiable","Local trimmed-face measurements are missing or incomplete.");
            var evidence=new Dictionary<string,string>();var failures=new List<string>();bool incomplete=false;
            var matchedAreas=new Dictionary<int,double>();
            var areaErrors=new Dictionary<int,double>();
            for(int i=0;i<points.Count;i++)
            {
                var probe=measured[i];
                if(!probe.Complete||probe.Faces.Count==0||probe.Faces.Any(f=>!Finite(f.ClosestPointMm)))
                {incomplete=true;failures.Add($"Point {i}: {probe.Error??"incomplete boundary measurement"}.");continue;}
                var nearest=probe.Faces.Min(f=>Norm(Sub(f.ClosestPointMm,points[i])));
                evidence[$"point_{i}_mm"]=Point(points[i]);evidence[$"point_{i}_nearest_boundary_mm"]=F(nearest);
                if(clearance is not null)
                {
                    if(nearest+clearance.ToleranceMm<clearance.MinimumDistanceMm) failures.Add($"Point {i}: boundary is closer than the required clearance.");
                    continue;
                }
                var check=surface!;var normal=Unit(check.OutwardNormals[i]);
                var candidates=probe.Faces.Where(f=>Norm(Sub(f.ClosestPointMm,points[i]))<=check.ToleranceMm&&f.Kind==check.SurfaceKind).ToArray();
                var valid=candidates.Where(f=>Finite(f.OutwardNormal)&&Norm(f.OutwardNormal)>1e-12&&
                    (check.DiameterMm is null||f.DiameterMm is {} d&&Positive(d))&&
                    (check.ConeHalfAngleDegrees is null||f.ConeHalfAngleDegrees is {} a&&Positive(a))).ToArray();
                if(valid.Length<candidates.Length) {incomplete=true;failures.Add($"Point {i}: invalid native surface parameters.");continue;}
                evidence[$"point_{i}_candidates"]=string.Join("; ",valid.Select(f=>$"{f.Kind}; normal {Point(f.OutwardNormal)}; diameter {f.DiameterMm}; half_angle {f.ConeHalfAngleDegrees}"));
                var matches=valid.Where(f=>Dot(Unit(f.OutwardNormal),normal)+1e-12>=Math.Cos(check.AngleToleranceDegrees*Math.PI/180)&&
                    (check.DiameterMm is null||Math.Abs(f.DiameterMm!.Value-check.DiameterMm.Value)<=check.ToleranceMm)&&
                    (check.ConeHalfAngleDegrees is null||Math.Abs(f.ConeHalfAngleDegrees!.Value-check.ConeHalfAngleDegrees.Value)<=check.AngleToleranceDegrees)).ToArray();
                if(matches.Length==0)
                    failures.Add($"Point {i}: no actual trimmed {check.SurfaceKind} face matches position, outward normal and declared parameters.");
                if(check.ExpectedAreaMm2 is not null)
                    foreach(var match in matches)
                    {
                        if(match.FaceIndex is not {} faceId||faceId<0||match.AreaMm2 is not {} area||!Positive(area)||
                            !double.IsFinite(match.AreaErrorMm2)||match.AreaErrorMm2<0||
                            matchedAreas.TryGetValue(faceId,out var prior)&&Math.Abs(prior-area)>1e-8)
                        {incomplete=true;failures.Add($"Point {i}: face area or identity is missing/inconsistent.");}
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
                if(difference-estimatedError>surface.AreaToleranceMm2)failures.Add("Total area of unique sampled faces differs from the source-derived area.");
                else if(difference+estimatedError>surface.AreaToleranceMm2)
                {incomplete=true;failures.Add("Area measurement is too close to the tolerance boundary for its estimated numerical precision.");}
            }
            var passed=failures.Count==0;
            evidence["sample_count"]=points.Count.ToString();
            evidence["scope"]=surface is not null?"Finite surface samples plus optional total area of touched faces; not exact whole-feature topology.":"Distance to solid boundary only; does not classify material/void or prove connectivity.";
            return new(id,passed,passed?"passed":incomplete?"unverifiable":"mismatch",passed?"All declared local samples match.":string.Join(" ",failures),evidence);
        }
        foreach(var c in spec.SurfaceSamples)yield return Evaluate(c.Id,c.PointsMm,c,null);
        foreach(var c in spec.BoundaryClearances)yield return Evaluate(c.Id,c.PointsMm,null,c);
    }
}
