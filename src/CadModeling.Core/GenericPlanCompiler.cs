using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<GenericOperationKind>))]
public enum GenericOperationKind
{
    ProfileSketch,
    ExtrudeBoss,
    ExtrudeCut,
    NativeFeature
}

[JsonConverter(typeof(JsonStringEnumConverter<GenericPrimitiveKind>))]
public enum GenericPrimitiveKind
{
    CenteredRectangle,
    ThreePointRectangle,
    Circle,
    Polygon,
    CompositeCurve,
    Ellipse,
    OpenCurve,
    Spline,
    Slot,
    Points
}

[JsonConverter(typeof(JsonStringEnumConverter<GenericCurveKind>))]
public enum GenericCurveKind
{
    Line,
    ThreePointArc
}

/// <summary>
/// Host-model-authored, tool-schema-constrained draft. It is intentionally not executable IR:
/// the compiler normalizes it into Modeling IR and geometry validation and native execution check the resulting operations.
/// </summary>
public sealed record GenericModelDraft
{
    public DrawingPlanContext? DrawingContext { get; init; }
    public string? SourceModelPath { get; init; }
    public required string Name { get; init; }
    public required string SourceText { get; init; }
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public required IReadOnlyList<GenericOperationDraft> Operations { get; init; }
    public BoundingBoxSpec? ExpectedBoundingBoxMm { get; init; }
    public double BoundingBoxToleranceMm { get; init; } = 0.1;
    public int ExpectedSolidBodyCount { get; init; } = 1;
    public int? ExpectedSurfaceBodyCount { get; init; }
    public double? ExpectedVolumeMm3 { get; init; }
    public double VolumeTolerancePercent { get; init; } = 0.5;
    public Vector3? ExpectedCenterOfMassMm { get; init; }
    public double CenterOfMassToleranceMm { get; init; } = 0.1;
}

public sealed record GenericOperationDraft
{
    public IReadOnlyList<SketchConstraintSpec> Constraints { get; init; } = [];
    public IReadOnlyList<SketchDimensionSpec> Dimensions { get; init; } = [];
    public IReadOnlyList<SketchEditSpec> Edits { get; init; } = [];
    public NativeFeatureOptions? Feature { get; init; }
    public SketchFrame? Frame { get; init; }
    public string? PlaneId { get; init; }
    public bool Merge { get; init; } = true;
    public required GenericOperationKind Type { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    // profile_sketch fields
    public ReferencePlane? Plane { get; init; }
    public GenericFaceReferenceDraft? FaceAttachment { get; init; }
    public IReadOnlyList<GenericPrimitiveDraft> Primitives { get; init; } = [];

    // extrude fields
    public string? SketchId { get; init; }
    public double? DepthMm { get; init; }
    public ExtrudeEndCondition? EndCondition { get; init; }
    public GenericFaceReferenceDraft? EndReference { get; init; }
    public double StartOffsetMm { get; init; }
    public bool ReverseStartOffset { get; init; }
    public bool ReverseDirection { get; init; }
}

public sealed record GenericPrimitiveDraft
{
    public bool Construction { get; init; }
    public bool Closed { get; init; }
    public required GenericPrimitiveKind Type { get; init; }
    public ContourRole Role { get; init; } = ContourRole.Outer;
    public double? CenterXmm { get; init; }
    public double? CenterYmm { get; init; }
    public double? WidthMm { get; init; }
    public double? HeightMm { get; init; }
    public double? DiameterMm { get; init; }
    public IReadOnlyList<ProfilePoint> Points { get; init; } = [];
    public IReadOnlyList<GenericCurveDraft> Curves { get; init; } = [];
}

public sealed record GenericCurveDraft
{
    public required GenericCurveKind Type { get; init; }
    public required ProfilePoint Start { get; init; }
    public required ProfilePoint End { get; init; }
    public ProfilePoint? PointOnArc { get; init; }
}

public sealed record GenericFaceReferenceDraft
{
    public required string SupportOperationId { get; init; }
    public required double PickXmm { get; init; }
    public required double PickYmm { get; init; }
    public required double PickZmm { get; init; }
}

public sealed partial class GenericPlanCompiler
{
    public CompilationResult Compile(
        GenericModelDraft draft,
        string? nativeOutputPath = null,
        IReadOnlyList<string>? exportPaths = null,
        bool overwriteAllowed = false)
    {
        var diagnostics = new List<ModelingDiagnostic>();
        diagnostics.AddRange(DrawingPlanValidation.Validate(draft));
        if (string.IsNullOrWhiteSpace(draft.Name))
            diagnostics.Add(Error("GPC001", "A generic model draft requires a name.", "draft.name"));
        if (string.IsNullOrWhiteSpace(draft.SourceText))
            diagnostics.Add(Error("GPC002", "A generic model draft requires source_text for traceability.", "draft.source_text"));
        if (draft.Operations.Count == 0)
            diagnostics.Add(Error("GPC003", "A generic model draft requires at least one operation.", "draft.operations"));

        var operations = new List<ModelingOperation>();
        for (var index = 0; index < draft.Operations.Count; index++)
        {
            var source = draft.Operations[index];
            var path = $"draft.operations[{index}]";
            var operation = CompileOperation(source, path, diagnostics);
            if (operation is not null) operations.Add(operation);
        }

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new(null, diagnostics);

        var plan = new ModelingPlan
        {
            PlanId = $"plan_{Guid.NewGuid():N}",
            Name = draft.Name.Trim(),
            SourceText = draft.SourceText.Trim(),
            SourceModelPath = draft.SourceModelPath,
            DrawingContext = draft.DrawingContext,
            Assumptions = draft.Assumptions,
            Operations = operations,
            Output = new()
            {
                NativePath = nativeOutputPath,
                ExportPaths = exportPaths ?? [],
                OverwriteAllowed = overwriteAllowed
            },
            Acceptance = new()
            {
                ExpectedFeatures = operations.Where(item => item is not NativeFeatureOperation n ||
                    n.Options.Kind is not (NativeFeatureKind.SetDimension or NativeFeatureKind.Suppress or NativeFeatureKind.Restore or NativeFeatureKind.Flatten))
                    .Select(item => item.Name).ToArray(),
                ExpectedBoundingBoxMm = draft.ExpectedBoundingBoxMm,
                BoundingBoxToleranceMm = draft.BoundingBoxToleranceMm,
                Geometry = new()
                {
                    ExpectedSolidBodyCount = draft.ExpectedSolidBodyCount,
                    ExpectedSurfaceBodyCount = draft.ExpectedSurfaceBodyCount,
                    RequirePositiveVolume = draft.ExpectedSolidBodyCount > 0,
                    ExpectedVolumeMm3 = draft.ExpectedVolumeMm3,
                    ExpectedCenterOfMassMm=draft.ExpectedCenterOfMassMm,
                    CenterOfMassToleranceMm=draft.CenterOfMassToleranceMm,
                    VolumeTolerancePercent = draft.VolumeTolerancePercent
                }
            }
        };

        var validation = new ModelingIrValidator().Validate(plan, forExecution: false);
        diagnostics.AddRange(validation.Diagnostics);
        if (diagnostics.All(item => item.Severity != DiagnosticSeverity.Error))
            diagnostics.Add(new("GPC000", DiagnosticSeverity.Info,
                "Typed draft compiled to executable Modeling IR."));
        return new(plan, diagnostics);
    }

    private static ModelingOperation? CompileOperation(
        GenericOperationDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(source.Id))
            diagnostics.Add(Error("GPC010", "Operation id is required.", $"{path}.id"));
        if (string.IsNullOrWhiteSpace(source.Name))
            diagnostics.Add(Error("GPC011", "Operation name is required.", $"{path}.name"));

        return source.Type switch
        {
            GenericOperationKind.ProfileSketch => CompileSketch(source, path, diagnostics),
            GenericOperationKind.ExtrudeBoss => CompileExtrude(source, path, diagnostics, isBoss: true),
            GenericOperationKind.ExtrudeCut => CompileExtrude(source, path, diagnostics, isBoss: false),
            GenericOperationKind.NativeFeature when source.Feature is not null => CompileNativeFeature(source),
            _ => RejectOperation(source, path, diagnostics)
        };
    }

    private static ModelingOperation? CompileSketch(
        GenericOperationDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        if (source.Plane is null && source.Frame is null && source.PlaneId is null)
            diagnostics.Add(Error("GPC020", "A profile sketch requires plane.", $"{path}.plane"));
        if (source.Primitives.Count == 0)
            diagnostics.Add(Error("GPC021", "A profile sketch requires at least one primitive.", $"{path}.primitives"));

        var primitives = new List<ProfilePrimitive>();
        for (var index = 0; index < source.Primitives.Count; index++)
        {
            var primitive = CompilePrimitive(source.Primitives[index], $"{path}.primitives[{index}]", diagnostics);
            if (primitive is not null) primitives.Add(primitive);
        }

        if ((source.Plane is null && source.Frame is null && source.PlaneId is null) || primitives.Count != source.Primitives.Count)
            return null;

        var dependencies = source.DependsOn.ToList();
        if (source.PlaneId is not null) AddDependency(dependencies, source.PlaneId);
        PlanarFaceAttachment? attachment = null;
        if (source.FaceAttachment is { } face)
        {
            AddDependency(dependencies, face.SupportOperationId);
            attachment = new()
            {
                SupportOperationId = face.SupportOperationId,
                PickXmm = face.PickXmm,
                PickYmm = face.PickYmm,
                PickZmm = face.PickZmm
            };
        }

        return new ProfileSketchOperation
        {
            Constraints = source.Constraints,
            Dimensions = source.Dimensions,
            Edits = source.Edits,
            Id = source.Id,
            Name = source.Name,
            DependsOn = dependencies,
            Plane = source.Plane ?? ReferencePlane.Front,
            Frame = source.Frame,
            PlaneId = source.PlaneId,
            FaceAttachment = attachment,
            Primitives = primitives
        };
    }

    private static ModelingOperation? CompileExtrude(
        GenericOperationDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics,
        bool isBoss)
    {
        if (string.IsNullOrWhiteSpace(source.SketchId))
            diagnostics.Add(Error("GPC030", "An extrude requires sketch_id.", $"{path}.sketch_id"));
        if (source.EndCondition is null)
            diagnostics.Add(Error("GPC031", "An extrude requires an explicit end_condition.", $"{path}.end_condition"));
        if (source.DepthMm is null)
            diagnostics.Add(Error("GPC032", "An extrude requires an explicit depth_mm; use 0 only for UpToSurface.", $"{path}.depth_mm"));
        if (string.IsNullOrWhiteSpace(source.SketchId) || source.EndCondition is null || source.DepthMm is null)
            return null;

        var dependencies = source.DependsOn.ToList();
        AddDependency(dependencies, source.SketchId);
        PlanarFaceReference? endReference = null;
        if (source.EndReference is { } face)
        {
            AddDependency(dependencies, face.SupportOperationId);
            endReference = new()
            {
                SupportOperationId = face.SupportOperationId,
                PickXmm = face.PickXmm,
                PickYmm = face.PickYmm,
                PickZmm = face.PickZmm
            };
        }

        return isBoss
            ? new ExtrudeBossOperation
            {
                Merge = source.Merge,
                Id = source.Id,
                Name = source.Name,
                DependsOn = dependencies,
                SketchId = source.SketchId,
                DepthMm = source.DepthMm.Value,
                EndCondition = source.EndCondition.Value,
                EndReference = endReference,
                StartOffsetMm = source.StartOffsetMm,
                ReverseStartOffset = source.ReverseStartOffset,
                ReverseDirection = source.ReverseDirection
            }
            : new ExtrudeCutOperation
            {
                Id = source.Id,
                Name = source.Name,
                DependsOn = dependencies,
                SketchId = source.SketchId,
                DepthMm = source.DepthMm.Value,
                EndCondition = source.EndCondition.Value,
                EndReference = endReference,
                StartOffsetMm = source.StartOffsetMm,
                ReverseStartOffset = source.ReverseStartOffset,
                ReverseDirection = source.ReverseDirection
            };
    }

    private static ProfilePrimitive? CompilePrimitive(
        GenericPrimitiveDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        switch (source.Type)
        {
            case GenericPrimitiveKind.Slot:
                if (source.Points.Count != 2 || source.WidthMm is not > 0)
                { diagnostics.Add(Error("SLOT_PARAMETERS", "Slot requires two arc-center points and positive width_mm.", path)); return null; }
                var start = source.Points[0]; var end = source.Points[1]; var radius = source.WidthMm.Value / 2;
                var dx = end.Xmm - start.Xmm; var dy = end.Ymm - start.Ymm; var length = Math.Sqrt(dx*dx+dy*dy);
                if (length < 1e-9) { diagnostics.Add(Error("SLOT_LENGTH", "Slot centers must differ.",path)); return null; }
                ProfilePoint Shift(ProfilePoint p, double u, double v) => new(p.Xmm+(dx*u-dy*v)/length, p.Ymm+(dy*u+dx*v)/length);
                return new CompositeCurveProfile { Role=source.Role, Curves=[
                    new LineProfileCurve { Start=Shift(start,0,radius),End=Shift(end,0,radius) },
                    new ThreePointArcProfileCurve { Start=Shift(end,0,radius),PointOnArc=Shift(end,radius,0),End=Shift(end,0,-radius) },
                    new LineProfileCurve { Start=Shift(end,0,-radius),End=Shift(start,0,-radius) },
                    new ThreePointArcProfileCurve { Start=Shift(start,0,-radius),PointOnArc=Shift(start,-radius,0),End=Shift(start,0,radius) }
                ] };
            case GenericPrimitiveKind.Ellipse:
                if (!RequireCenter(source, path, diagnostics) || source.WidthMm is null || source.HeightMm is null)
                {
                    diagnostics.Add(Error("GPC050", "Ellipse requires explicit center, width_mm and height_mm.", path));
                    return null;
                }
                return new EllipseProfile { CenterXmm = source.CenterXmm!.Value, CenterYmm = source.CenterYmm!.Value,
                    MajorRadiusMm = source.WidthMm.Value / 2, MinorRadiusMm = source.HeightMm.Value / 2, Role = source.Role };
            case GenericPrimitiveKind.Spline:
                return new SplineProfile { Role = source.Role, Points = source.Points, Closed = source.Closed };
            case GenericPrimitiveKind.Points:
                return new SketchPointsProfile { Role=source.Role, Points=source.Points };
            case GenericPrimitiveKind.OpenCurve:
                if (source.Curves.Any(c => c.Type == GenericCurveKind.ThreePointArc && c.PointOnArc is null))
                {
                    diagnostics.Add(Error("GPC051", "Every arc requires point_on_arc.", path));
                    return null;
                }
                return new OpenCurveProfile { Role = source.Role, Construction = source.Construction,
                    Curves = source.Curves.Select(c => c.Type == GenericCurveKind.Line
                        ? (ProfileCurve)new LineProfileCurve { Start = c.Start, End = c.End }
                        : new ThreePointArcProfileCurve { Start = c.Start, End = c.End, PointOnArc = c.PointOnArc! }).ToArray() };
            case GenericPrimitiveKind.CenteredRectangle:
                if (!RequireCenter(source, path, diagnostics) || source.WidthMm is null || source.HeightMm is null)
                {
                    if (source.WidthMm is null) diagnostics.Add(Error("GPC040", "A centered rectangle requires width_mm.", $"{path}.width_mm"));
                    if (source.HeightMm is null) diagnostics.Add(Error("GPC041", "A centered rectangle requires height_mm.", $"{path}.height_mm"));
                    return null;
                }
                return new CenteredRectangleProfile
                {
                    Role = source.Role,
                    CenterXmm = source.CenterXmm!.Value,
                    CenterYmm = source.CenterYmm!.Value,
                    WidthMm = source.WidthMm.Value,
                    HeightMm = source.HeightMm.Value
                };

            case GenericPrimitiveKind.ThreePointRectangle:
                if (source.Points.Count != 3)
                {
                    diagnostics.Add(Error("GPC042", "A three-point rectangle requires exactly three ordered points.", $"{path}.points"));
                    return null;
                }
                return new ThreePointRectangleProfile
                {
                    Role = source.Role,
                    Corner1 = source.Points[0],
                    Corner2 = source.Points[1],
                    Corner3 = source.Points[2]
                };

            case GenericPrimitiveKind.Circle:
                if (!RequireCenter(source, path, diagnostics) || source.DiameterMm is null)
                {
                    if (source.DiameterMm is null) diagnostics.Add(Error("GPC043", "A circle requires diameter_mm.", $"{path}.diameter_mm"));
                    return null;
                }
                return new CircleProfile
                {
                    Role = source.Role,
                    CenterXmm = source.CenterXmm!.Value,
                    CenterYmm = source.CenterYmm!.Value,
                    DiameterMm = source.DiameterMm.Value
                };

            case GenericPrimitiveKind.Polygon:
                if (source.Points.Count < 3)
                {
                    diagnostics.Add(Error("GPC044", "A polygon requires at least three ordered points.", $"{path}.points"));
                    return null;
                }
                return new PolygonProfile { Role = source.Role, Points = source.Points };

            case GenericPrimitiveKind.CompositeCurve:
                if (source.Curves.Count < 3)
                {
                    diagnostics.Add(Error("GPC045", "A composite curve requires at least three ordered curves.", $"{path}.curves"));
                    return null;
                }
                var curves = new List<ProfileCurve>();
                for (var index = 0; index < source.Curves.Count; index++)
                {
                    var curve = source.Curves[index];
                    var curvePath = $"{path}.curves[{index}]";
                    if (curve.Type == GenericCurveKind.ThreePointArc && curve.PointOnArc is null)
                    {
                        diagnostics.Add(Error("GPC046", "A three-point arc requires point_on_arc.", $"{curvePath}.point_on_arc"));
                        continue;
                    }
                    curves.Add(curve.Type == GenericCurveKind.Line
                        ? new LineProfileCurve { Start = curve.Start, End = curve.End }
                        : new ThreePointArcProfileCurve
                        {
                            Start = curve.Start,
                            PointOnArc = curve.PointOnArc!,
                            End = curve.End
                        });
                }
                return curves.Count == source.Curves.Count
                    ? new CompositeCurveProfile { Role = source.Role, Curves = curves }
                    : null;

            default:
                diagnostics.Add(Error("GPC049", $"Unsupported primitive type '{source.Type}'.", $"{path}.type"));
                return null;
        }
    }

    private static bool RequireCenter(
        GenericPrimitiveDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        var valid = true;
        if (source.CenterXmm is null)
        {
            diagnostics.Add(Error("GPC047", "This primitive requires explicit center_xmm.", $"{path}.center_xmm"));
            valid = false;
        }
        if (source.CenterYmm is null)
        {
            diagnostics.Add(Error("GPC048", "This primitive requires explicit center_ymm.", $"{path}.center_ymm"));
            valid = false;
        }
        return valid;
    }

    private static ModelingOperation? RejectOperation(
        GenericOperationDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        diagnostics.Add(Error("GPC019", $"Unsupported operation type '{source.Type}'.", $"{path}.type"));
        return null;
    }

    private static void AddDependency(List<string> dependencies, string id)
    {
        if (!dependencies.Contains(id, StringComparer.OrdinalIgnoreCase)) dependencies.Add(id);
    }

    private static ModelingDiagnostic Error(string code, string message, string path) =>
        new(code, DiagnosticSeverity.Error, message, path,
            "Correct the structured draft and compile again; do not bypass Modeling IR.");
}
