using CadModeling.Ir;
namespace CadModeling.Core;

public sealed partial class GenericPlanCompiler
{
    private static NativeFeatureOperation CompileNativeFeature(GenericOperationDraft source)
    {
        var options = source.Feature!;
        var dependencies = source.DependsOn.ToList();
        foreach (var id in options.ProfileIds.Concat(options.GuideIds)
            .Concat(new[] { options.SketchId, options.PathSketchId, options.AxisId })
            .Concat(options.Selections.Select(s => s.FeatureId)).OfType<string>()) AddDependency(dependencies, id);
        return new NativeFeatureOperation { Id = source.Id, Name = source.Name, DependsOn = dependencies, Options = options };
    }
}

public static class NativeFeatureValidation
{
    public static void Validate(NativeFeatureOperation operation, string path, List<ModelingDiagnostic> errors)
    {
        var o = operation.Options;
        void Require(bool ok, string message) { if (!ok) errors.Add(new("FEATURE_PARAMETER", DiagnosticSeverity.Error, message, path)); }
        bool Positive(double x) => double.IsFinite(x) && x > 0;
        foreach(var property in typeof(NativeFeatureOptions).GetProperties().Where(p=>p.PropertyType==typeof(double)))
            Require(double.IsFinite((double)property.GetValue(o)!), $"{property.Name} must be finite.");
        Require(Finite(o.TranslationMm) && Finite(o.RotationDegrees), "Body transform coordinates must be finite.");
        if (o.Frame is { } frame) ValidateFrame(frame, path, errors);
        foreach (var s in o.Selections)
        {
            Require(Positive(s.ToleranceMm), "Entity selection tolerance must be positive.");
            if (s.PositionMm is { } p) Require(Finite(p), "Selection point must be finite.");
            if (s.Direction is { } n) Require(Finite(n) && Norm(n) > 1e-10, "Selection direction must be nonzero.");
            if (s.RadiusMm is { } r) Require(Positive(r), "Selection radius must be positive.");
        }
        switch (o.Kind)
        {
            case NativeFeatureKind.ReferencePlane:
                Require(o.Frame is not null, "A reference plane requires an origin, normal and X direction."); break;
            case NativeFeatureKind.ReferenceAxis:
                Require(o.AxisStartMm is not null && o.AxisEndMm is not null, "Reference axis requires two different points.");
                if (o.AxisStartMm is { } a && o.AxisEndMm is { } b)
                    Require(Finite(a) && Finite(b) && Norm(new(a.X-b.X,a.Y-b.Y,a.Z-b.Z)) > 1e-9, "Axis points must be finite and different.");
                break;
            case NativeFeatureKind.Chamfer:
                Require(o.Selections.Count > 0 && Positive(o.DistanceMm), "Chamfer needs edge selections and positive distance.");
                if (o.ChamferMode == ChamferMode.DistanceAngle) Require(o.AngleDegrees > 0 && o.AngleDegrees < 90, "Chamfer angle must be between 0 and 90 degrees.");
                if (o.ChamferMode == ChamferMode.TwoDistances) Require(Positive(o.SecondDistanceMm), "Second chamfer distance must be positive.");
                break;
            case NativeFeatureKind.Fillet:
                Require(o.Selections.Count > 0 && Positive(o.RadiusMm), "Fillet needs selections and positive radius."); break;
            case NativeFeatureKind.RevolveBoss: case NativeFeatureKind.RevolveCut:
                Require(o.SketchId is not null && o.AxisId is not null, "Revolve requires a profile sketch and an axis.");
                Require(double.IsFinite(o.AngleDegrees) && o.AngleDegrees > 0 && o.AngleDegrees <= 360, "Revolve angle must be in (0,360].");
                Require(double.IsFinite(o.ThicknessMm) && o.ThicknessMm >= 0, "Thin revolve thickness cannot be negative."); break;
            case NativeFeatureKind.Shell: case NativeFeatureKind.Thicken:
                Require(Positive(o.ThicknessMm), "Thickness must be positive."); break;
            case NativeFeatureKind.LinearPattern:
                Require(o.Count >= 2 && Positive(o.SpacingMm), "Linear pattern needs count >=2 and positive spacing."); break;
            case NativeFeatureKind.CircularPattern:
                Require(o.Count >= 2 && o.AngleDegrees > 0 && o.AngleDegrees <= 360, "Circular pattern needs count >=2 and angle in (0,360]."); break;
            case NativeFeatureKind.LoftBoss: case NativeFeatureKind.LoftCut: case NativeFeatureKind.SurfaceLoft:
                Require(o.ProfileIds.Count >= 2, "Loft requires at least two profiles."); break;
            case NativeFeatureKind.SweepBoss: case NativeFeatureKind.SweepCut:
                Require(o.SketchId is not null && o.PathSketchId is not null, "Sweep requires profile and path sketches."); break;
            case NativeFeatureKind.Hole:
                Require(Positive(o.DiameterMm) && o.HoleCenters.Count > 0, "Hole requires positive diameter and positions.");
                Require(o.ThroughAll || Positive(o.DepthMm), "Blind hole depth must be positive.");
                Require(o.HoleCenters.All(p=>double.IsFinite(p.Xmm)&&double.IsFinite(p.Ymm)), "Hole centers must be finite.");
                if(o.HoleKind==HoleKind.Counterbore) Require(o.CounterboreDiameterMm>o.DiameterMm && Positive(o.CounterboreDepthMm), "Counterbore needs a larger diameter and positive depth.");
                if(o.HoleKind==HoleKind.Countersink) Require(o.CountersinkDiameterMm>o.DiameterMm && o.CountersinkAngleDegrees>0 && o.CountersinkAngleDegrees<180, "Countersink needs a larger diameter and included angle in (0,180).");
                if(o.HoleKind==HoleKind.Tapped) Require(o.ThreadMajorDiameterMm>o.DiameterMm && !string.IsNullOrWhiteSpace(o.ThreadDesignation), "Tapped hole needs explicit major diameter and thread designation; diameter_mm is the drill diameter.");
                break;
            case NativeFeatureKind.ThinExtrude: case NativeFeatureKind.SheetMetalBase: case NativeFeatureKind.Rib:
                Require(o.SketchId is not null && Positive(o.ThicknessMm), "This feature requires a sketch and positive thickness.");
                if(o.Kind==NativeFeatureKind.ThinExtrude) Require(Positive(o.DepthMm), "Thin extrusion depth must be positive.");
                if(o.Kind==NativeFeatureKind.SheetMetalBase) Require(o.RadiusMm>=0, "Bend radius cannot be negative."); break;
            case NativeFeatureKind.SurfaceExtrude:
                Require(o.SketchId is not null && Positive(o.DepthMm), "Surface extrusion requires a sketch and positive depth."); break;
            case NativeFeatureKind.WeldmentMember:
                Require(o.PathSketchId is not null && o.ProfilePath is not null && Path.IsPathFullyQualified(o.ProfilePath) && File.Exists(o.ProfilePath)
                    && o.ProfilePath.EndsWith(".sldlfp",StringComparison.OrdinalIgnoreCase), "Structural member requires a path sketch and an existing absolute SLDLFP profile path."); break;
            case NativeFeatureKind.TrimWeldment:
                Require(o.DistanceMm>=0 && o.Selections.Any(s=>s.SelectionMark==2) && o.Selections.Any(s=>s.SelectionMark!=2), "Weldment trim needs target bodies, mark-2 trimming tools, and a nonnegative gap."); break;
            case NativeFeatureKind.SurfaceTrim:
                Require(o.Selections.Any(s=>s.SelectionMark!=2) && o.Selections.Any(s=>s.SelectionMark==2), "Surface trim needs trimming tools and mark-2 regions to keep.");
                Require(o.Selections.Where(s=>s.SelectionMark==2).All(s=>s.Kind==EntityKind.Body && s.PositionMm is not null), "Each kept surface body needs a point inside its retained region."); break;
            case NativeFeatureKind.SurfacePlanar: case NativeFeatureKind.SketchPattern:
                Require(o.SketchId is not null, "This feature requires a sketch."); break;
            case NativeFeatureKind.EdgeFlange:
                Require(o.Selections.Count>0 && o.Selections.All(s=>s.Kind==EntityKind.Edge) && Positive(o.DistanceMm) && o.AngleDegrees>0 && o.AngleDegrees<180,
                    "Edge flange needs edges, a positive length, and an angle in (0,180)."); break;
            case NativeFeatureKind.Draft:
                Require(o.Selections.Count>1 && o.AngleDegrees>0 && o.AngleDegrees<90, "Draft needs a neutral plane, draft faces, and an angle in (0,90)."); break;
            case NativeFeatureKind.SetDimension:
                Require(o.DimensionName?.Split('@').Length>=2 && Positive(o.DimensionValue), "Dimension edit requires name@feature and positive value."); break;
        }
    }
    public static bool Finite(Vector3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
    public static double Norm(Vector3 v) => Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);
    public static void ValidateFrame(SketchFrame f, string path, List<ModelingDiagnostic> errors)
    {
        var cross = new Vector3(f.Normal.Y*f.XDirection.Z-f.Normal.Z*f.XDirection.Y,
            f.Normal.Z*f.XDirection.X-f.Normal.X*f.XDirection.Z,f.Normal.X*f.XDirection.Y-f.Normal.Y*f.XDirection.X);
        if (!Finite(f.OriginMm) || !Finite(f.Normal) || !Finite(f.XDirection) || Norm(cross) < 1e-10)
            errors.Add(new("FRAME_INVALID", DiagnosticSeverity.Error, "Sketch frame needs a finite origin and nonparallel nonzero normal/X directions.", path));
    }
}
