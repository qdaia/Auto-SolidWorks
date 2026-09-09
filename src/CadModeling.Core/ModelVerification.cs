using System.Globalization;
using System.Text.Json;
using CadModeling.Ir;
namespace CadModeling.Core;

public sealed record MeasuredCylinder(Vector3 AxisStartMm, Vector3 AxisEndMm, Vector3 Direction,
    double RadiusMm, double AreaMm2, bool Interior, string? FeatureName);
public sealed record VerificationCheckResult(string Id, bool Passed, string Status, string Message,
    IReadOnlyDictionary<string,string>? Measurements = null);
public sealed record ModelVerificationResult(IReadOnlyList<VerificationCheckResult> Checks)
{
    public bool Passed => Checks.Count>0 && Checks.All(c=>c.Passed);
    public string Scope => "Declared source requirements only; not complete drawing or GD&T equivalence.";
}

public static partial class ModelVerification
{
    public static bool HasChecks(ModelVerificationSpec spec) => spec.CylinderGroups.Count+spec.NativeDimensions.Count+spec.Bounds.Count+spec.SurfaceSamples.Count+spec.BoundaryClearances.Count>0;
    public static IEnumerable<ModelingDiagnostic> Validate(ModelVerificationSpec spec, DrawingPlanContext? context)
    {
        var errors=new List<ModelingDiagnostic>();
        void Error(string message) => errors.Add(new("VERIFICATION_SPEC",DiagnosticSeverity.Error,message,"verification"));
        var all=spec.CylinderGroups.Select(c=>(c.Id,c.SourceLiteral,c.SourceDimensionIds,(object)c))
            .Concat(spec.NativeDimensions.Select(c=>(c.Id,c.SourceLiteral,c.SourceDimensionIds,(object)c)))
            .Concat(spec.Bounds.Select(c=>(c.Id,c.SourceLiteral,c.SourceDimensionIds,(object)c)))
            .Concat(spec.SurfaceSamples.Select(c=>(c.Id,c.SourceLiteral,c.SourceDimensionIds,(object)c)))
            .Concat(spec.BoundaryClearances.Select(c=>(c.Id,c.SourceLiteral,c.SourceDimensionIds,(object)c))).ToArray();
        ValidateLocalChecks(spec,Error);
        if(all.Select(c=>c.Id).Distinct(StringComparer.Ordinal).Count()!=all.Length) Error("Verification check IDs must be unique.");
        foreach(var c in all)
        {
            if(string.IsNullOrWhiteSpace(c.Id)||string.IsNullOrWhiteSpace(c.SourceLiteral)) Error("Each check needs an ID and source requirement literal.");
            if(context is not null && (c.SourceDimensionIds.Count==0 || c.SourceDimensionIds.Any(id=>!context.Dimensions.Any(d=>d.Id==id))))
                Error($"Check '{c.Id}' needs existing source dimension IDs.");
            if(context is not null)
                foreach(var id in c.SourceDimensionIds)
                    if(!spec.Bindings.Any(b=>b.DimensionId==id&&b.CheckId==c.Id)) Error($"Check '{c.Id}' has no measured-parameter binding for source dimension '{id}'.");
        }
        foreach(var c in spec.CylinderGroups)
        {
            if(!Positive(c.DiameterMm)||!Positive(c.LengthMm)||!Positive(c.ToleranceMm)||!Finite(c.Direction)||Norm(c.Direction)<1e-12 ||
               !double.IsFinite(c.DirectionToleranceDegrees)||c.DirectionToleranceDegrees<0||c.DirectionToleranceDegrees>=90 ||
               c.AxisStartsMm.Count==0||c.AxisStartsMm.Any(p=>!Finite(p))) Error($"Cylinder check '{c.Id}' has invalid dimensions, direction, tolerance or axis starts.");
            for(var i=0;i<c.AxisStartsMm.Count;i++) for(var j=0;j<i;j++)
                if(Norm(Sub(c.AxisStartsMm[i],c.AxisStartsMm[j]))<=c.ToleranceMm) Error($"Cylinder check '{c.Id}' has duplicate expected cylinders.");
        }
        foreach(var c in spec.NativeDimensions)
            if(string.IsNullOrWhiteSpace(c.DimensionName)||!double.IsFinite(c.Value)||!Positive(c.Tolerance)||!Enum.IsDefined(c.Unit)) Error($"Native dimension check '{c.Id}' is invalid.");
        foreach(var c in spec.Bounds)
            if(!Finite(c.SizeMm)||c.SizeMm.X<0||c.SizeMm.Y<0||c.SizeMm.Z<0||!Positive(c.ToleranceMm)) Error($"Bounds check '{c.Id}' is invalid.");
        if(context is null && spec.Bindings.Count>0) Error("Source verification bindings require drawing_context.");
        foreach(var binding in spec.Bindings)
        {
            var fact=context?.Dimensions.FirstOrDefault(d=>d.Id==binding.DimensionId);
            var check=all.FirstOrDefault(c=>c.Id==binding.CheckId);
            if(fact is null || check.Item4 is null || !check.SourceDimensionIds.Contains(binding.DimensionId))
            { Error("Verification binding refers to an absent check or source dimension."); continue; }
            var element=JsonSerializer.SerializeToElement(check.Item4,check.Item4.GetType(),ModelingIrJson.Options);
            var found=TryField(element,binding.ParameterPath,out var value);
            var sourceValue=fact.Value*UnitFactor(fact.Unit);
            var isAngle=check.Item4 is NativeDimensionCheck { Unit: DrawingValueUnit.Degree } || check.Item4 is SurfaceSampleCheck && binding.ParameterPath is "cone_half_angle_degrees" or "cone_included_angle_degrees";
            var isCount=binding.ParameterPath=="expected_count"||check.Item4 is NativeDimensionCheck { Unit: DrawingValueUnit.Unitless };
            if(check.Item4 is NativeDimensionCheck dimension && binding.ParameterPath=="value") value*=UnitFactor(dimension.Unit);
            var allowed=binding.ParameterPath=="value"&&check.Item4 is NativeDimensionCheck ||
                check.Item4 is BoundsCheck && binding.ParameterPath.StartsWith("size_mm.",StringComparison.Ordinal) ||
                check.Item4 is CylinderGroupCheck && (binding.ParameterPath is "diameter_mm" or "length_mm" or "expected_count" || binding.ParameterPath.StartsWith("axis_starts_mm.",StringComparison.Ordinal)) ||
                check.Item4 is SurfaceSampleCheck && (binding.ParameterPath is "diameter_mm" or "cone_half_angle_degrees" or "cone_included_angle_degrees" || binding.ParameterPath.StartsWith("points_mm.",StringComparison.Ordinal)) ||
                check.Item4 is BoundaryClearanceCheck && (binding.ParameterPath=="minimum_distance_mm" || binding.ParameterPath.StartsWith("points_mm.",StringComparison.Ordinal));
            if(!allowed || !found || !double.IsFinite(value) || isAngle!=(fact.Unit==DrawingValueUnit.Degree) || isCount!=(fact.Unit==DrawingValueUnit.Unitless) || Math.Abs(value-sourceValue)>1e-7*Math.Max(1,Math.Abs(sourceValue)))
                Error($"Check '{binding.CheckId}' parameter '{binding.ParameterPath}' differs from source dimension '{binding.DimensionId}', or uses an invalid field/unit.");
        }
        return errors;
    }

    public static ModelVerificationResult Evaluate(ModelVerificationSpec spec, IReadOnlyList<MeasuredCylinder> cylinders,
        IReadOnlyDictionary<string,double> nativeSystemValues, GeometrySnapshot? geometry,
        IReadOnlyDictionary<string,IReadOnlyList<LocalPointMeasurement>>? localMeasurements=null)
    {
        var results=new List<VerificationCheckResult>();
        results.AddRange(EvaluateLocalChecks(spec,localMeasurements));
        foreach(var check in spec.CylinderGroups) results.Add(CheckCylinders(check,cylinders));
        foreach(var check in spec.NativeDimensions)
        {
            if(!nativeSystemValues.TryGetValue(check.DimensionName,out var actual)||!double.IsFinite(actual))
            { results.Add(new(check.Id,false,"unverifiable","The saved model does not expose the requested active native dimension.")); continue; }
            var factor=check.Unit==DrawingValueUnit.Degree ? 180/Math.PI : check.Unit==DrawingValueUnit.Unitless?1:1000/UnitFactor(check.Unit);
            var measured=actual*factor;
            var passed=Math.Abs(measured-check.Value)<=check.Tolerance;
            results.Add(new(check.Id,passed,passed?"passed":"mismatch","Read the native model dimension.",new Dictionary<string,string>{["name"]=check.DimensionName,["measured"]=F(measured),["expected"]=F(check.Value),["unit"]=check.Unit.ToString()}));
        }
        foreach(var check in spec.Bounds)
        {
            var b=geometry?.BoundingBoxMm;
            var passed=b is not null&&Finite(new(b.X,b.Y,b.Z))&&Math.Abs(b.X-check.SizeMm.X)<=check.ToleranceMm&&Math.Abs(b.Y-check.SizeMm.Y)<=check.ToleranceMm&&Math.Abs(b.Z-check.SizeMm.Z)<=check.ToleranceMm;
            results.Add(new(check.Id,passed,b is null?"unverifiable":passed?"passed":"mismatch","Measured the actual model bounding box.",new Dictionary<string,string>{["measured_mm"]=b is null?"unavailable":$"{F(b.X)}, {F(b.Y)}, {F(b.Z)}",["expected_mm"]=Point(check.SizeMm)}));
        }
        return new(results);
    }

    private sealed record CylinderSpan(Vector3 Lateral, double Start, double End, double Area, double Radius);
    private static VerificationCheckResult CheckCylinders(CylinderGroupCheck check,IReadOnlyList<MeasuredCylinder> measurements)
    {
        var direction=Unit(check.Direction); var cos=Math.Cos(check.DirectionToleranceDegrees*Math.PI/180);
        var spans=new List<CylinderSpan>();
        foreach(var cylinder in measurements)
        {
            if(!Finite(cylinder.AxisStartMm)||!Finite(cylinder.AxisEndMm)||!Finite(cylinder.Direction)||!Positive(cylinder.RadiusMm)||!Positive(cylinder.AreaMm2))
                return new(check.Id,false,"unverifiable","Cylinder measurement contains invalid geometry.");
            if(cylinder.Interior!=check.Interior||Math.Abs(cylinder.RadiusMm*2-check.DiameterMm)>check.ToleranceMm || Math.Abs(Dot(Unit(cylinder.Direction),direction))+1e-12<cos) continue;
            var a=Dot(cylinder.AxisStartMm,direction); var b=Dot(cylinder.AxisEndMm,direction);
            spans.Add(new(Sub(cylinder.AxisStartMm,Scale(direction,a)),Math.Min(a,b),Math.Max(a,b),cylinder.AreaMm2,cylinder.RadiusMm));
        }
        // Merge adjacent axial patches and seam-split faces, not disjoint coaxial blind holes.
        var merged=new List<CylinderSpan>();
        foreach(var span in spans.OrderBy(s=>s.Start))
        {
            var index=merged.FindIndex(s=>Norm(Sub(s.Lateral,span.Lateral))<=check.ToleranceMm&&span.Start<=s.End+check.ToleranceMm&&span.End>=s.Start-check.ToleranceMm);
            if(index<0) merged.Add(span);
            else {var prior=merged[index];merged[index]=prior with {Start=Math.Min(prior.Start,span.Start),End=Math.Max(prior.End,span.End),Area=prior.Area+span.Area};}
        }
        var used=new HashSet<int>(); var failures=new List<string>(); bool incomplete=false;
        foreach(var start in check.AxisStartsMm)
        {
            var end=Add(start,Scale(direction,check.LengthMm));
            var t0=Dot(start,direction); var t1=Dot(end,direction); var lateral=Sub(start,Scale(direction,t0));
            var matches=merged.Select((m,i)=>(m,i)).Where(x=>Norm(Sub(x.m.Lateral,lateral))<=check.ToleranceMm &&
                Math.Abs(x.m.Start-Math.Min(t0,t1))<=check.ToleranceMm&&Math.Abs(x.m.End-Math.Max(t0,t1))<=check.ToleranceMm).ToArray();
            if(matches.Length!=1 || used.Contains(matches.FirstOrDefault().i)&&matches.Length==1)
            {failures.Add($"No unique cylinder with the requested axial extent at {Point(start)}.");continue;}
            var (actual,index)=matches[0];used.Add(index);
            var fullArea=2*Math.PI*actual.Radius*(actual.End-actual.Start);
            // Partial/open/intersected cylindrical walls are not certified as complete drilled holes.
            if(fullArea<=0||Math.Abs(actual.Area/fullArea-1)>0.002)
            {incomplete=true;failures.Add($"Cylinder at {Point(start)} is partial or intersected; full cylindrical wall cannot be verified.");}
        }
        if(check.ExactCount&&merged.Count!=check.AxisStartsMm.Count) failures.Add($"Expected {check.AxisStartsMm.Count} cylinders of this diameter/direction, measured {merged.Count}.");
        var passed=failures.Count==0;
        return new(check.Id,passed,passed?"passed":incomplete?"unverifiable":"mismatch",passed?"Actual cylindrical walls match diameter, axis, axial start/end and count.":string.Join(" ",failures),
            new Dictionary<string,string>{["expected_count"]=check.AxisStartsMm.Count.ToString(CultureInfo.InvariantCulture),["measured_count"]=merged.Count.ToString(CultureInfo.InvariantCulture),
                ["measured_axes_and_spans_mm"]=string.Join("; ",merged.Select(m=>$"axis {Point(m.Lateral)}; t {F(m.Start)}..{F(m.End)}; diameter {F(m.Radius*2)}"))});
    }
    private static bool TryField(JsonElement node,string path,out double value)
    {
        value=0;
        foreach(var part in path.Split('.'))
        {
            if(node.ValueKind==JsonValueKind.Object&&node.TryGetProperty(part,out var child)) node=child;
            else if(node.ValueKind==JsonValueKind.Array&&int.TryParse(part,out var i)&&i>=0&&i<node.GetArrayLength()) node=node[i];
            else return false;
        }
        return node.ValueKind==JsonValueKind.Number&&node.TryGetDouble(out value);
    }
    public static double UnitFactor(DrawingValueUnit unit)=>unit switch {DrawingValueUnit.Inch=>25.4,DrawingValueUnit.Meter=>1000,_=>1};
    public static bool Finite(Vector3 v)=>double.IsFinite(v.X)&&double.IsFinite(v.Y)&&double.IsFinite(v.Z);
    public static bool Positive(double v)=>double.IsFinite(v)&&v>0;
    public static double Dot(Vector3 a,Vector3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    public static Vector3 Add(Vector3 a,Vector3 b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static Vector3 Sub(Vector3 a,Vector3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static Vector3 Scale(Vector3 a,double s)=>new(a.X*s,a.Y*s,a.Z*s);
    public static double Norm(Vector3 v)=>Math.Sqrt(Dot(v,v));
    public static Vector3 Unit(Vector3 v)=>Scale(v,1/Norm(v));
    private static string F(double value)=>value.ToString("G12",CultureInfo.InvariantCulture);
    private static string Point(Vector3 v)=>$"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";
}
