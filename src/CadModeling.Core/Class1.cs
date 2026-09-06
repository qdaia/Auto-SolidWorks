using System.Globalization;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CadModeling.Ir;

namespace CadModeling.Core;

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record ModelingDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Path = null,
    string? SuggestedAction = null);

public sealed record CompilationResult(
    ModelingPlan? Plan,
    IReadOnlyList<ModelingDiagnostic> Diagnostics)
{
    public bool Success => Plan is not null && Diagnostics.All(x => x.Severity != DiagnosticSeverity.Error);
}

public sealed record ValidationReport(IReadOnlyList<ModelingDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(x => x.Severity != DiagnosticSeverity.Error);
}

public sealed record ExecutorHealth(
    bool Available,
    string Executor,
    string Message,
    string? SolidWorksRevision = null,
    string? ActiveDocument = null);

public sealed record ExecutionEvidence(
    string Stage,
    string Message,
    bool Passed,
    IReadOnlyDictionary<string, string>? Data = null,
    string? Code = null,
    ExecutionFailureCategory? Category = null,
    bool Retryable = false,
    string? SuggestedAction = null,
    string? OperationId = null);

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionFailureCategory>))]
public enum ExecutionFailureCategory
{
    Validation,
    ReferenceResolution,
    FeatureCreation,
    GeometryQuality,
    Rebuild,
    Output,
    Preview,
    SolidWorksInterop,
    Environment,
    Transport,
    Unknown
}

public sealed record GeometrySnapshot(
    int SolidBodyCount,
    int FaceCount,
    int EdgeCount,
    double VolumeMm3,
    double SurfaceAreaMm2,
    SpatialPoint CenterOfMassMm,
    BoundingBoxSpec BoundingBoxMm)
{
    public int SurfaceBodyCount { get; init; }
}

public sealed record SpatialPoint(double X, double Y, double Z);

public sealed record StableFeatureReference(
    string OperationId,
    string OperationName,
    string OperationType,
    string SolidWorksName,
    int? SolidWorksFeatureId,
    string? PersistentReferenceBase64,
    IReadOnlyList<string> DependsOn);

public sealed record ExecutionResult(
    bool Success,
    string Status,
    string Message,
    IReadOnlyList<ExecutionEvidence> Evidence,
    string? NativePath = null,
    GeometrySnapshot? Geometry = null,
    IReadOnlyList<StableFeatureReference>? FeatureReferences = null,
    string? PlanFingerprint = null);

public sealed record ModelingCapabilities(
    string SchemaVersion,
    IReadOnlyList<string> DocumentKinds,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> NaturalLanguageExamples,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> PostflightChecks,
    IReadOnlyDictionary<string, string> RepairGuidance,
    IReadOnlyList<DrawingCapabilityClassification> DrawingCapabilities);

public sealed record DrawingCapabilityClassification(
    string Capability,
    bool Recognized,
    bool Interpretable,
    bool Reasonable,
    bool Plannable,
    bool Executable,
    bool Repairable,
    string Status,
    string Boundary);

public static class ModelingCapabilityCatalog
{
    public static ModelingCapabilities Current { get; } = new(
        ModelingIrSchema.CurrentVersion,
        ["part", "assembly"],
        new[] {
            "ProfileSketch: rectangle, circle, polygon, line/arc contours, slot, ellipse, spline, points; native constraints, dimensions, offset and trim",
            "Sketch coordinates: standard planes, planar faces, arbitrary frames and reference planes",
            "ExtrudeBoss/ExtrudeCut: Blind, MidPlane, ThroughAll, UpToNext, UpToSurface",
            "Inspection: features, dimensions, configurations, geometry, persistent entity references; source-preserving part edits",
            "Assembly: components, transforms, geometric mates, interference and saved-state verification"
        }.Concat(Enum.GetNames<NativeFeatureKind>()).ToArray(),
        ["Use cad_create_model_plan with a typed draft for any supported feature combination.",
         "做一个长80宽50厚10毫米的矩形板，中心开一个直径10毫米通孔",
         "80 x 50 x 10 mm plate with a centered diameter 10 mm through hole",
         "cylinder diameter 40 mm height 60 mm"],
        ["All dimensions default to mm; typed rotations and angles use degrees.",
         "Drawing OCR returns candidates. The agent interprets view geometry and binds dimensions before planning; low-resolution OCR may be wrong.",
         "Tapped holes use a drilled hole and cosmetic thread annotation; physical helical threads are not generated.",
         "Native SLDPRT and SLDASM creation is supported; native drawing-sheet generation is not implemented.",
         "Free-form surfacing uses supplied profiles/paths, not arbitrary automatic shape reconstruction.",
         "Creation success does not certify equivalence to a source drawing. Individual variants require valid SolidWorks geometry."],
        ["Native rebuild and feature creation", "Body counts, volume, area, centroid and bounding box",
         "Requested dimensional tolerances", "Saved native files and exports", "Persistent feature references",
         "Assembly reopen: components, transforms, fixed state and mate identities"],
        new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            ["validation"]="Correct the reported typed field and recompile.",
            ["reference_resolution"]="Inspect current entities; constrain the query with feature ownership, geometry, position or radius.",
            ["feature_creation"]="Use the operation id to inspect its profile, selections and dimensions; revise the plan and rebuild a new output.",
            ["geometry_quality"]="Compare actual bodies and measurements with the requested shape; inspect disconnected or missing features.",
            ["rebuild"]="Correct the first failing native feature and its dependencies.",
            ["output"]="Choose a writable versioned local output path.",
            ["solid_works_interop"]="Check the executor connection and SolidWorks state before retrying."
        },
        [new("engineering_dimension_candidates",true,true,true,true,true,true,"agent_interpretation",
            "OCR candidates retain source locations. Image interpretation must establish feature attachment and resolve conflicting dimensions."),
         new("geometric_feature_planning",true,true,true,true,true,true,"typed_native_features",
            "Model parts and assemblies using supported typed features and explicit geometry."),
         new("physical_threads_and_gdandt",true,false,false,false,false,false,"limited",
            "Cosmetic thread annotations are available; physical threads and general GD&T interpretation are not implemented.")]);
}

public interface IModelingExecutor
{
    Task<AssemblyResult> BuildAssemblyAsync(AssemblyPlan plan,CancellationToken cancellationToken=default) =>
        Task.FromResult(new AssemblyResult(false,"This executor does not support assemblies."));
    Task<ModelInspection> InspectAsync(ModelInspectionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModelInspection(false, "This executor does not support inspection.", request.InputPath));
    Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default);
    Task<ExecutionResult> ExecuteAsync(ModelingPlan plan, bool dryRun, CancellationToken cancellationToken = default);
}

public sealed class ModelingIrValidator
{
    public ValidationReport Validate(ModelingPlan plan, bool forExecution = false)
    {
        var diagnostics = new List<ModelingDiagnostic>();
        ErrorIf(!ModelingIrSchema.IsSupported(plan.SchemaVersion), "IR001",
            $"Unsupported schema_version '{plan.SchemaVersion}'. Supported versions: {string.Join(", ", ModelingIrSchema.SupportedVersions)}.", "schema_version");
        ErrorIf(string.IsNullOrWhiteSpace(plan.PlanId), "IR002", "plan_id is required.", "plan_id");
        ErrorIf(string.IsNullOrWhiteSpace(plan.Name), "IR003", "name is required.", "name");
        ErrorIf(plan.DocumentKind != DocumentKind.Part, "IR004", "The MVP executor currently supports part documents only.", "document_kind");
        ErrorIf(plan.Operations.Count == 0, "IR005", "At least one modeling operation is required.", "operations");

        var seen = new Dictionary<string, ModelingOperation>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < plan.Operations.Count; index++)
        {
            var operation = plan.Operations[index];
            var path = $"operations[{index}]";
            ErrorIf(string.IsNullOrWhiteSpace(operation.Id), "IR010", "Operation id is required.", $"{path}.id");
            ErrorIf(!seen.TryAdd(operation.Id, operation), "IR011", $"Duplicate operation id '{operation.Id}'.", $"{path}.id");
            ErrorIf(string.IsNullOrWhiteSpace(operation.Name), "IR012", "Operation name is required.", $"{path}.name");

            foreach (var dependency in operation.DependsOn)
            {
                ErrorIf(!seen.ContainsKey(dependency), "IR013",
                    $"Dependency '{dependency}' must refer to an earlier operation.", $"{path}.depends_on");
            }

            switch (operation)
            {
                case ProfileSketchOperation sketch:
                    if (sketch.Frame is { } frame) NativeFeatureValidation.ValidateFrame(frame, path, diagnostics);
                    ValidateSketch(sketch, path, diagnostics);
                    break;
                case ExtrudeBossOperation extrude:
                    ValidateExtrude(extrude.SketchId, extrude.DepthMm, extrude.EndCondition,
                        extrude.EndReference, extrude.StartOffsetMm, extrude.ReverseStartOffset,
                        operation, path, seen, diagnostics);
                    break;
                case ExtrudeCutOperation cut:
                    ValidateExtrude(cut.SketchId, cut.DepthMm, cut.EndCondition,
                        cut.EndReference, cut.StartOffsetMm, cut.ReverseStartOffset,
                        operation, path, seen, diagnostics);
                    break;
                case NativeFeatureOperation native:
                    NativeFeatureValidation.Validate(native, path, diagnostics);
                    break;
                default:
                    diagnostics.Add(new("IR099", DiagnosticSeverity.Error,
                        $"Unsupported operation CLR type '{operation.GetType().Name}'.", path));
                    break;
            }
        }

        var nativePath = plan.Output.NativePath;
        if (plan.SourceModelPath is { } source)
        {
            ErrorIf(!Path.IsPathFullyQualified(source) || !File.Exists(source), "SOURCE_MODEL", "Source model must be an existing absolute file path.", "source_model_path");
            ErrorIf(!source.EndsWith(".sldprt",StringComparison.OrdinalIgnoreCase), "SOURCE_FORMAT", "Source editing currently requires a native SLDPRT file.", "source_model_path");
            ErrorIf(Path.IsPathFullyQualified(source) && nativePath is not null && Path.IsPathFullyQualified(nativePath) && Path.GetFullPath(source).Equals(Path.GetFullPath(nativePath), StringComparison.OrdinalIgnoreCase),
                "SOURCE_MODEL_OUTPUT", "Editing uses a separate output version; source and output paths must differ.", "output.native_path");
        }
        if (forExecution && string.IsNullOrWhiteSpace(nativePath))
            diagnostics.Add(new("OUT001", DiagnosticSeverity.Error, "A native_path is required for execution.", "output.native_path"));

        if (!string.IsNullOrWhiteSpace(nativePath))
        {
            ErrorIf(!Path.IsPathFullyQualified(nativePath), "OUT002", "native_path must be absolute.", "output.native_path");
            ErrorIf(!nativePath.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase), "OUT003",
                "The part executor requires a .SLDPRT native_path.", "output.native_path");
            ErrorIf(File.Exists(nativePath) && !plan.Output.OverwriteAllowed, "OUT004",
                "The native output already exists and overwrite_allowed is false.", "output.native_path",
                "Choose a versioned path or explicitly authorize overwrite.");
        }

        for (var index = 0; index < plan.Output.ExportPaths.Count; index++)
        {
            var exportPath = plan.Output.ExportPaths[index];
            var path = $"output.export_paths[{index}]";
            ErrorIf(!Path.IsPathFullyQualified(exportPath), "OUT011", "Export path must be absolute.", path);
            var extension = Path.GetExtension(exportPath);
            ErrorIf(!new[] { ".step", ".stp", ".stl" }.Contains(extension, StringComparer.OrdinalIgnoreCase),
                "OUT012", "Supported export extensions are .STEP, .STP, and .STL.", path);
            ErrorIf(File.Exists(exportPath) && !plan.Output.OverwriteAllowed, "OUT013",
                "The export output already exists and overwrite_allowed is false.", path,
                "Choose a versioned path or explicitly authorize overwrite.");
        }

        ValidateAcceptance(plan.Acceptance, diagnostics);

        return new(diagnostics);

        void ErrorIf(bool condition, string code, string message, string path, string? action = null)
        {
            if (condition) diagnostics.Add(new(code, DiagnosticSeverity.Error, message, path, action));
        }
    }

    private static void ValidateAcceptance(AcceptanceSpec acceptance, List<ModelingDiagnostic> diagnostics)
    {
        if (acceptance.ExpectedBoundingBoxMm is { } bounds &&
            (!double.IsFinite(bounds.X) || bounds.X<0 || !double.IsFinite(bounds.Y) || bounds.Y<0 || !double.IsFinite(bounds.Z) || bounds.Z<0))
            diagnostics.Add(new("ACC001", DiagnosticSeverity.Error,
                "Expected bounding-box dimensions must be finite and nonnegative.", "acceptance.expected_bounding_box_mm"));
        if (!double.IsFinite(acceptance.BoundingBoxToleranceMm) || acceptance.BoundingBoxToleranceMm < 0)
            diagnostics.Add(new("ACC002", DiagnosticSeverity.Error,
                "Bounding-box tolerance must be finite and non-negative.", "acceptance.bounding_box_tolerance_mm"));
        if (acceptance.Geometry.ExpectedSolidBodyCount < 0 || acceptance.Geometry.ExpectedSurfaceBodyCount < 0 ||
            acceptance.Geometry.ExpectedSolidBodyCount == 0 && !(acceptance.Geometry.ExpectedSurfaceBodyCount > 0))
            diagnostics.Add(new("ACC010", DiagnosticSeverity.Error,
                "Expected counts must be nonnegative; a surface-only model requires a positive expected surface-body count.", "acceptance.geometry.expected_solid_body_count"));
        if (acceptance.Geometry.ExpectedVolumeMm3 is { } volume && !IsFinitePositive(volume))
            diagnostics.Add(new("ACC011", DiagnosticSeverity.Error,
                "Expected volume must be finite and positive when specified.", "acceptance.geometry.expected_volume_mm3"));
        if (!double.IsFinite(acceptance.Geometry.VolumeTolerancePercent) || acceptance.Geometry.VolumeTolerancePercent < 0)
            diagnostics.Add(new("ACC012", DiagnosticSeverity.Error,
                "Volume tolerance percent must be finite and non-negative.", "acceptance.geometry.volume_tolerance_percent"));

        static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;
        if(acceptance.Geometry.ExpectedCenterOfMassMm is { } center && !NativeFeatureValidation.Finite(center) ||
            !double.IsFinite(acceptance.Geometry.CenterOfMassToleranceMm) || acceptance.Geometry.CenterOfMassToleranceMm<0)
            diagnostics.Add(new("ACC013",DiagnosticSeverity.Error,"Center-of-mass expectation and tolerance must be finite; tolerance cannot be negative.","acceptance.geometry"));
    }

    private static void ValidateSketch(ProfileSketchOperation sketch, string path, List<ModelingDiagnostic> diagnostics)
    {
        if (sketch.Primitives.Count == 0)
            diagnostics.Add(new("IR020", DiagnosticSeverity.Error, "A profile sketch needs at least one primitive.", $"{path}.primitives"));
        if (!sketch.Primitives.Any(x => x.Role == ContourRole.Outer))
            diagnostics.Add(new("IR021", DiagnosticSeverity.Error, "A profile sketch needs at least one outer contour.", $"{path}.primitives"));

        if (sketch.FaceAttachment is { } attachment)
        {
            if (string.IsNullOrWhiteSpace(attachment.SupportOperationId))
                diagnostics.Add(new("IR040", DiagnosticSeverity.Error,
                    "A face attachment requires a preceding support operation id.", $"{path}.face_attachment.support_operation_id"));
            else if (!sketch.DependsOn.Contains(attachment.SupportOperationId, StringComparer.OrdinalIgnoreCase))
                diagnostics.Add(new("IR041", DiagnosticSeverity.Error,
                    "A face-attached sketch must list its support operation in depends_on.", $"{path}.depends_on"));

            if (!double.IsFinite(attachment.PickXmm) || !double.IsFinite(attachment.PickYmm) || !double.IsFinite(attachment.PickZmm))
                diagnostics.Add(new("IR042", DiagnosticSeverity.Error,
                    "Face attachment pick coordinates must be finite millimetre values.", $"{path}.face_attachment"));
        }

        for (var index = 0; index < sketch.Primitives.Count; index++)
        {
            var primitive = sketch.Primitives[index];
            var primitivePath = $"{path}.primitives[{index}]";
            if (!double.IsFinite(primitive.CenterXmm) || !double.IsFinite(primitive.CenterYmm))
                diagnostics.Add(new("IR022", DiagnosticSeverity.Error, "Profile center coordinates must be finite.", primitivePath));
            switch (primitive)
            {
                case EllipseProfile e when !double.IsFinite(e.MajorRadiusMm) || !double.IsFinite(e.MinorRadiusMm) || e.MajorRadiusMm <= 0 || e.MinorRadiusMm <= 0:
                    diagnostics.Add(new("IR050", DiagnosticSeverity.Error, "Ellipse radii must be positive.", primitivePath)); break;
                case OpenCurveProfile open:
                    if (open.Curves.Count == 0) diagnostics.Add(new("IR051", DiagnosticSeverity.Error, "Open curve must contain segments.", primitivePath));
                    foreach (var curve in open.Curves)
                        if (!double.IsFinite(curve.Start.Xmm) || !double.IsFinite(curve.Start.Ymm) || !double.IsFinite(curve.End.Xmm) || !double.IsFinite(curve.End.Ymm))
                            diagnostics.Add(new("IR052", DiagnosticSeverity.Error, "Curve points must be finite.", primitivePath));
                    break;
                case SplineProfile spline:
                    if (spline.Points.Count < 2 || spline.Points.Any(p => !double.IsFinite(p.Xmm) || !double.IsFinite(p.Ymm)))
                        diagnostics.Add(new("IR053", DiagnosticSeverity.Error, "Spline needs at least two finite points.", primitivePath));
                    break;
                case SketchPointsProfile pointSet:
                    if(pointSet.Points.Count==0 || pointSet.Points.Any(p=>!double.IsFinite(p.Xmm)||!double.IsFinite(p.Ymm)))
                        diagnostics.Add(new("IR054",DiagnosticSeverity.Error,"Sketch point set requires finite points.",primitivePath));
                    break;
                case CenteredRectangleProfile rectangle when rectangle.WidthMm <= 0 || rectangle.HeightMm <= 0 ||
                                                               !double.IsFinite(rectangle.WidthMm) || !double.IsFinite(rectangle.HeightMm):
                    diagnostics.Add(new("IR023", DiagnosticSeverity.Error, "Rectangle dimensions must be finite and positive.", primitivePath));
                    break;
                case CircleProfile circle when circle.DiameterMm <= 0 || !double.IsFinite(circle.DiameterMm):
                    diagnostics.Add(new("IR024", DiagnosticSeverity.Error, "Circle diameter must be finite and positive.", primitivePath));
                    break;
                case ThreePointRectangleProfile rectangle:
                    var points = new[] { rectangle.Corner1, rectangle.Corner2, rectangle.Corner3 };
                    if (points.Any(point => !double.IsFinite(point.Xmm) || !double.IsFinite(point.Ymm)))
                    {
                        diagnostics.Add(new("IR028", DiagnosticSeverity.Error, "Three-point rectangle coordinates must be finite.", primitivePath));
                        break;
                    }
                    var cross = (rectangle.Corner2.Xmm - rectangle.Corner1.Xmm) * (rectangle.Corner3.Ymm - rectangle.Corner2.Ymm) -
                                (rectangle.Corner2.Ymm - rectangle.Corner1.Ymm) * (rectangle.Corner3.Xmm - rectangle.Corner2.Xmm);
                    if (Math.Abs(cross) < 1e-9)
                        diagnostics.Add(new("IR029", DiagnosticSeverity.Error, "Three-point rectangle corners must define a non-zero area.", primitivePath));
                    break;
                case PolygonProfile polygon:
                    if (polygon.Points.Count < 3)
                    {
                        diagnostics.Add(new("IR025", DiagnosticSeverity.Error, "A polygon needs at least three points.", primitivePath));
                        break;
                    }
                    if (polygon.Points.Any(point => !double.IsFinite(point.Xmm) || !double.IsFinite(point.Ymm)))
                        diagnostics.Add(new("IR026", DiagnosticSeverity.Error, "Polygon point coordinates must be finite.", primitivePath));
                    var twiceArea = polygon.Points.Select((point, pointIndex) =>
                    {
                        var next = polygon.Points[(pointIndex + 1) % polygon.Points.Count];
                        return point.Xmm * next.Ymm - next.Xmm * point.Ymm;
                    }).Sum();
                    if (Math.Abs(twiceArea) < 1e-9)
                        diagnostics.Add(new("IR027", DiagnosticSeverity.Error, "Polygon area must be non-zero.", primitivePath));
                    break;
                case CompositeCurveProfile composite:
                    ValidateCompositeCurve(composite, primitivePath, diagnostics);
                    break;
            }
        }
    }

    private static void ValidateCompositeCurve(
        CompositeCurveProfile composite,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        if (composite.Curves.Count < 3)
        {
            diagnostics.Add(new("IR050", DiagnosticSeverity.Error,
                "A composite curve contour needs at least three connected segments.", $"{path}.curves"));
            return;
        }

        for (var index = 0; index < composite.Curves.Count; index++)
        {
            var curve = composite.Curves[index];
            var curvePath = $"{path}.curves[{index}]";
            if (!IsFinite(curve.Start) || !IsFinite(curve.End))
                diagnostics.Add(new("IR051", DiagnosticSeverity.Error,
                    "Composite-curve endpoints must be finite.", curvePath));
            if (SamePoint(curve.Start, curve.End))
                diagnostics.Add(new("IR052", DiagnosticSeverity.Error,
                    "Composite-curve segments cannot have coincident endpoints.", curvePath));
            if (curve is ThreePointArcProfileCurve arc)
            {
                if (!IsFinite(arc.PointOnArc))
                    diagnostics.Add(new("IR053", DiagnosticSeverity.Error,
                        "The three-point arc point must be finite.", curvePath));
                else if (AreCollinear(arc.Start, arc.PointOnArc, arc.End))
                    diagnostics.Add(new("IR054", DiagnosticSeverity.Error,
                        "A three-point arc requires a non-collinear point on the arc.", curvePath));
            }

            var next = composite.Curves[(index + 1) % composite.Curves.Count];
            if (!SamePoint(curve.End, next.Start))
                diagnostics.Add(new("IR055", DiagnosticSeverity.Error,
                    "Composite-curve segments must be ordered as one closed, endpoint-connected contour.", curvePath));
        }

        static bool IsFinite(ProfilePoint point) => double.IsFinite(point.Xmm) && double.IsFinite(point.Ymm);
        static bool SamePoint(ProfilePoint first, ProfilePoint second) =>
            Math.Abs(first.Xmm - second.Xmm) <= 1e-7 && Math.Abs(first.Ymm - second.Ymm) <= 1e-7;
        static bool AreCollinear(ProfilePoint first, ProfilePoint second, ProfilePoint third) =>
            Math.Abs((second.Xmm - first.Xmm) * (third.Ymm - first.Ymm) -
                     (second.Ymm - first.Ymm) * (third.Xmm - first.Xmm)) <= 1e-9;
    }

    private static void ValidateExtrude(
        string sketchId,
        double depthMm,
        ExtrudeEndCondition endCondition,
        PlanarFaceReference? endReference,
        double startOffsetMm,
        bool reverseStartOffset,
        ModelingOperation operation,
        string path,
        IReadOnlyDictionary<string, ModelingOperation> seen,
        List<ModelingDiagnostic> diagnostics)
    {
        if (endCondition is ExtrudeEndCondition.Blind or ExtrudeEndCondition.MidPlane &&
            (depthMm <= 0 || !double.IsFinite(depthMm)))
            diagnostics.Add(new("IR030", DiagnosticSeverity.Error,
                "Blind and mid-plane extrude depth must be a finite positive millimetre value.", $"{path}.depth_mm"));
        if (endCondition == ExtrudeEndCondition.UpToSurface &&
            (!double.IsFinite(depthMm) || depthMm < 0))
            diagnostics.Add(new("IR030", DiagnosticSeverity.Error,
                "Up-to-surface fallback depth must be finite and non-negative.", $"{path}.depth_mm"));
        if (endCondition is not (ExtrudeEndCondition.Blind or ExtrudeEndCondition.MidPlane or ExtrudeEndCondition.UpToSurface or ExtrudeEndCondition.ThroughAll or ExtrudeEndCondition.UpToNext))
            diagnostics.Add(new("IR033", DiagnosticSeverity.Error,
                "Unsupported extrude end condition.", $"{path}.end_condition"));
        if (endCondition == ExtrudeEndCondition.UpToSurface && endReference is null)
            diagnostics.Add(new("IR034", DiagnosticSeverity.Error,
                "An up-to-surface extrude requires an end_reference.", $"{path}.end_reference"));
        if (endCondition != ExtrudeEndCondition.UpToSurface && endReference is not null)
            diagnostics.Add(new("IR035", DiagnosticSeverity.Error,
                "end_reference is valid only for an UpToSurface extrude.", $"{path}.end_reference"));
        if (!double.IsFinite(startOffsetMm) || startOffsetMm < 0)
            diagnostics.Add(new("IR036", DiagnosticSeverity.Error,
                "Start offset must be a finite non-negative millimetre value.", $"{path}.start_offset_mm"));
        if (startOffsetMm == 0 && reverseStartOffset)
            diagnostics.Add(new("IR037", DiagnosticSeverity.Error,
                "reverse_start_offset requires a positive start_offset_mm.", $"{path}.reverse_start_offset"));
        if (endReference is { } reference)
        {
            if (string.IsNullOrWhiteSpace(reference.SupportOperationId) ||
                !seen.ContainsKey(reference.SupportOperationId))
                diagnostics.Add(new("IR043", DiagnosticSeverity.Error,
                    "The end-reference support must be an earlier operation.", $"{path}.end_reference.support_operation_id"));
            else if (!operation.DependsOn.Contains(reference.SupportOperationId, StringComparer.OrdinalIgnoreCase))
                diagnostics.Add(new("IR044", DiagnosticSeverity.Error,
                    "An up-to-surface extrude must list its end-reference support in depends_on.", $"{path}.depends_on"));
            if (!double.IsFinite(reference.PickXmm) || !double.IsFinite(reference.PickYmm) || !double.IsFinite(reference.PickZmm))
                diagnostics.Add(new("IR045", DiagnosticSeverity.Error,
                    "End-reference pick coordinates must be finite millimetre values.", $"{path}.end_reference"));
        }
        if (!seen.TryGetValue(sketchId, out var sketchOp) || sketchOp is not ProfileSketchOperation)
            diagnostics.Add(new("IR031", DiagnosticSeverity.Error,
                $"sketch_id '{sketchId}' must refer to an earlier profile_sketch operation.", $"{path}.sketch_id"));
        if (!operation.DependsOn.Contains(sketchId, StringComparer.OrdinalIgnoreCase))
            diagnostics.Add(new("IR032", DiagnosticSeverity.Error,
                "An extrude operation must list its sketch_id in depends_on.", $"{path}.depends_on"));
    }
}

public sealed partial class RuleBasedTextCompiler
{
    public CompilationResult Compile(string text, string? nativeOutputPath = null)
    {
        var diagnostics = new List<ModelingDiagnostic>();
        if (string.IsNullOrWhiteSpace(text))
            return new(null, [new("NL001", DiagnosticSeverity.Error, "Natural-language input is empty.", "text")]);

        var normalized = Normalize(text);
        var assumptions = new List<string>();
        if (!MentionsUnitRegex().IsMatch(normalized))
            assumptions.Add("A1. Unqualified lengths are interpreted as millimetres.");

        ModelingPlan? plan = ForkBracketIntentRegex().IsMatch(normalized)
            ? TryCompileForkBracket(normalized, text, nativeOutputPath, assumptions, diagnostics)
            : LChannelIntentRegex().IsMatch(normalized)
                ? TryCompileLChannel(normalized, text, nativeOutputPath, assumptions, diagnostics)
                : TryCompilePlate(normalized, text, nativeOutputPath, assumptions, diagnostics)
                  ?? TryCompileCylinder(normalized, text, nativeOutputPath, assumptions, diagnostics);

        if (plan is null)
        {
            diagnostics.Add(new("NL100", DiagnosticSeverity.Error,
                "The MVP compiler could not identify a supported part with complete dimensions.", "text",
                "Use '80 x 50 x 10 mm plate' or 'diameter 40 mm, height 60 mm cylinder'."));
            return new(null, diagnostics);
        }

        diagnostics.Add(new("NL000", DiagnosticSeverity.Info,
            "Natural language compiled to deterministic Modeling IR. Review assumptions before execution."));
        return new(plan, diagnostics);
    }

    private static ModelingPlan? TryCompileForkBracket(
        string normalized,
        string sourceText,
        string? output,
        IReadOnlyList<string> assumptions,
        List<ModelingDiagnostic> diagnostics)
    {
        var baseDimensions = ForkBaseDimensionsRegex().Match(normalized);
        var mountingDiameter = ForkMountingHoleDiameterRegex().Match(normalized);
        var mountingSpacing = ForkMountingHoleSpacingRegex().Match(normalized);
        var earOuterWidth = ForkEarOuterWidthRegex().Match(normalized);
        var earGap = ForkEarGapRegex().Match(normalized);
        var pinDiameter = ForkPinHoleDiameterRegex().Match(normalized);
        var pinHeight = ForkPinCenterHeightRegex().Match(normalized);
        var headRadius = ForkHeadRadiusRegex().Match(normalized);
        var pinToEarEnd = ForkPinToEarEndRegex().Match(normalized);
        var stepToBaseEnd = ForkStepToBaseEndRegex().Match(normalized);
        var earEndHeight = ForkEarEndHeightRegex().Match(normalized);

        if (!baseDimensions.Success || !mountingDiameter.Success || !mountingSpacing.Success ||
            !earOuterWidth.Success || !earGap.Success || !pinDiameter.Success || !pinHeight.Success ||
            !headRadius.Success || !pinToEarEnd.Success || !stepToBaseEnd.Success || !earEndHeight.Success)
        {
            diagnostics.Add(new("NL110", DiagnosticSeverity.Error,
                "The fork-bracket intent was recognized, but one or more critical dimensions are missing.", "text",
                "Specify base W×D×T, mounting-hole diameter and spacing, ear outer width and inner gap, pin-hole diameter, pin-center height, head radius, pin-to-ear-end, step-to-base-end, and ear-end height."));
            return null;
        }

        var baseWidth = Number(baseDimensions, "w");
        var baseDepth = Number(baseDimensions, "d");
        var baseThickness = Number(baseDimensions, "t");
        var mountDiameter = Number(mountingDiameter, "v");
        var mountSpacing = Number(mountingSpacing, "v");
        var outerWidth = Number(earOuterWidth, "v");
        var innerGap = Number(earGap, "v");
        var pinHoleDiameter = Number(pinDiameter, "v");
        var pinCenterFromBottom = Number(pinHeight, "v");
        var radius = Number(headRadius, "v");
        var earEndFromPin = Number(pinToEarEnd, "v");
        var stepFromBaseEnd = Number(stepToBaseEnd, "v");
        var endHeightFromBottom = Number(earEndHeight, "v");
        var stepFromPin = baseDepth - stepFromBaseEnd;

        var dimensions = new[]
        {
            baseWidth, baseDepth, baseThickness, mountDiameter, mountSpacing, outerWidth, innerGap,
            pinHoleDiameter, pinCenterFromBottom, radius, earEndFromPin, stepFromBaseEnd, endHeightFromBottom
        };
        if (dimensions.Any(value => value <= 0 || !double.IsFinite(value)) ||
            innerGap >= outerWidth || mountSpacing >= baseWidth || mountDiameter >= baseDepth ||
            pinHoleDiameter >= radius * 2 || stepFromPin <= 0 || stepFromPin >= earEndFromPin ||
            earEndFromPin > baseDepth || endHeightFromBottom <= baseThickness ||
            pinCenterFromBottom - radius <= baseThickness)
        {
            diagnostics.Add(new("NL111", DiagnosticSeverity.Error,
                "The fork-bracket dimensions are geometrically inconsistent.", "text",
                "Check hole diameters, ear gap/outer width, mounting-hole spacing, step position, end height, and R-head clearance above the base."));
            return null;
        }

        var explicitAssumptions = assumptions.ToList();
        explicitAssumptions.Add("A2. The two Ø9 base mounting holes are placed on the 36 mm depth centerline because the drawing does not dimension their longitudinal location.");
        explicitAssumptions.Add("A3. The two ear holes are modeled as coaxial Ø8 through cuts from the clearly readable side-view callout; no additional concentric boss is inferred from the low-resolution center-mark detail.");
        explicitAssumptions.Add("A4. Unspecified edge breaks remain sharp; the R10 lug head is created directly in the side profile.");
        explicitAssumptions.Add("A5. The right-plane ear outline is a closed construction-order contour: 6 mm vertical line, lower sloped line, R-head semicircle, upper sloped line, end vertical, then a bottom closing line.");

        var baseTop = baseThickness / 2d;
        var pinZ = pinCenterFromBottom - baseThickness / 2d;
        var endTop = endHeightFromBottom - baseThickness / 2d;
        // Right-plane sketch coordinates are mirrored in the SolidWorks standard-right view.
        // Keep the round head at the drawing's left and the dimensioned ear end at its right.
        var pinU = baseDepth;
        var slotMinY = pinU - earEndFromPin - 1d;
        var slotMaxY = pinU + radius + 1d;
        var slotMinZ = baseTop;
        var slotMaxZ = pinZ + radius + 1d;

        var baseSketch = new ProfileSketchOperation
        {
            Id = "sketch_base",
            Name = "Base_Profile",
            Plane = ReferencePlane.Top,
            Primitives =
            [
                new CenteredRectangleProfile
                {
                    Role = ContourRole.Outer,
                    CenterXmm = 0d,
                    CenterYmm = baseDepth / 2d,
                    WidthMm = baseWidth,
                    HeightMm = baseDepth
                }
            ]
        };
        var baseBoss = new ExtrudeBossOperation
        {
            Id = "boss_base", Name = "Base_Boss", DependsOn = [baseSketch.Id], SketchId = baseSketch.Id,
            DepthMm = baseThickness, EndCondition = ExtrudeEndCondition.MidPlane
        };
        var footAtStep = new ProfilePoint(pinU - stepFromPin, baseTop);
        var topOfSixMillimeterVertical = new ProfilePoint(pinU - stepFromPin, baseTop + 6d);
        var lowerSemiCircleEnd = new ProfilePoint(pinU, pinZ - radius);
        var upperSemiCircleEnd = new ProfilePoint(pinU, pinZ + radius);
        var leftArcPoint = new ProfilePoint(pinU + radius, pinZ);
        var topAtEarEnd = new ProfilePoint(pinU - earEndFromPin, endTop);
        var footAtEarEnd = new ProfilePoint(pinU - earEndFromPin, baseTop);
        var earProfileSketch = new ProfileSketchOperation
        {
            Id = "sketch_lug_right_plane", Name = "Lug_Right_Plane_Profile", Plane = ReferencePlane.Right,
            DependsOn = [baseBoss.Id],
            Primitives =
            [
                new CompositeCurveProfile
                {
                    Role = ContourRole.Outer,
                    Curves =
                    [
                        new LineProfileCurve { Start = footAtStep, End = topOfSixMillimeterVertical },
                        new LineProfileCurve { Start = topOfSixMillimeterVertical, End = lowerSemiCircleEnd },
                        new ThreePointArcProfileCurve { Start = lowerSemiCircleEnd, PointOnArc = leftArcPoint, End = upperSemiCircleEnd },
                        new LineProfileCurve { Start = upperSemiCircleEnd, End = topAtEarEnd },
                        new LineProfileCurve { Start = topAtEarEnd, End = footAtEarEnd },
                        new LineProfileCurve { Start = footAtEarEnd, End = footAtStep }
                    ]
                }
            ]
        };
        var earProfileBoss = new ExtrudeBossOperation
        {
            Id = "boss_lug_ears", Name = "Lug_Ears_Boss", DependsOn = [earProfileSketch.Id, baseBoss.Id],
            SketchId = earProfileSketch.Id, DepthMm = outerWidth, EndCondition = ExtrudeEndCondition.MidPlane
        };
        var slotSketch = new ProfileSketchOperation
        {
            Id = "sketch_center_slot", Name = "Center_Slot_Profile", Plane = ReferencePlane.Right,
            DependsOn = [earProfileBoss.Id],
            Primitives =
            [
                new CenteredRectangleProfile
                {
                    CenterXmm = (slotMinY + slotMaxY) / 2d,
                    CenterYmm = (slotMinZ + slotMaxZ) / 2d,
                    WidthMm = slotMaxY - slotMinY,
                    HeightMm = slotMaxZ - slotMinZ
                }
            ]
        };
        var slotCut = new ExtrudeCutOperation
        {
            Id = "cut_center_slot", Name = "Center_Slot_Cut", DependsOn = [slotSketch.Id, earProfileBoss.Id],
            SketchId = slotSketch.Id, DepthMm = innerGap, EndCondition = ExtrudeEndCondition.MidPlane
        };
        var pinSketch = new ProfileSketchOperation
        {
            Id = "sketch_pin_holes", Name = "Pin_Hole_Profile", Plane = ReferencePlane.Right,
            DependsOn = [slotCut.Id],
            Primitives = [new CircleProfile { DiameterMm = pinHoleDiameter, CenterXmm = pinU, CenterYmm = pinZ }]
        };
        var pinCut = new ExtrudeCutOperation
        {
            Id = "cut_pin_holes", Name = "Pin_Hole_Cut", DependsOn = [pinSketch.Id, slotCut.Id],
            SketchId = pinSketch.Id, DepthMm = outerWidth + 4d, EndCondition = ExtrudeEndCondition.MidPlane
        };
        var mountingSketch = new ProfileSketchOperation
        {
            Id = "sketch_mounting_holes", Name = "Mounting_Hole_Profile", Plane = ReferencePlane.Top,
            DependsOn = [pinCut.Id],
            Primitives =
            [
                new CircleProfile { DiameterMm = mountDiameter, CenterXmm = -mountSpacing / 2d, CenterYmm = baseDepth / 2d },
                new CircleProfile { DiameterMm = mountDiameter, CenterXmm = mountSpacing / 2d, CenterYmm = baseDepth / 2d }
            ]
        };
        var mountingCut = new ExtrudeCutOperation
        {
            Id = "cut_mounting_holes", Name = "Mounting_Hole_Cut", DependsOn = [mountingSketch.Id, pinCut.Id],
            SketchId = mountingSketch.Id, DepthMm = baseThickness + 4d, EndCondition = ExtrudeEndCondition.MidPlane
        };

        ModelingOperation[] operations =
        [
            baseSketch, baseBoss, earProfileSketch, earProfileBoss,
            slotSketch, slotCut, pinSketch, pinCut, mountingSketch, mountingCut
        ];
        var plan = NewPlan("fork_bracket", sourceText, output, explicitAssumptions, operations,
            new(baseWidth, pinCenterFromBottom + radius, baseDepth + radius));
        return output is null
            ? plan
            : plan with { Output = plan.Output with { ExportPaths = [Path.ChangeExtension(output, ".step")] } };
    }

    private static ModelingPlan? TryCompilePlate(
        string normalized,
        string sourceText,
        string? output,
        IReadOnlyList<string> assumptions,
        List<ModelingDiagnostic> diagnostics)
    {
        var labelled = ChinesePlateRegex().Match(normalized);
        var triplet = DimensionTripletRegex().Match(normalized);
        if (!labelled.Success && !triplet.Success)
            return null;

        var match = labelled.Success ? labelled : triplet;
        var width = Number(match, "x");
        var height = Number(match, "y");
        var depth = Number(match, "z");
        if (width <= 0 || height <= 0 || depth <= 0) return null;

        var profiles = new List<ProfilePrimitive>
        {
            new CenteredRectangleProfile { Role = ContourRole.Outer, WidthMm = width, HeightMm = height }
        };

        if (HoleIntentRegex().IsMatch(normalized))
        {
            var centered = CenterIntentRegex().IsMatch(normalized);
            var through = ThroughHoleIntentRegex().IsMatch(normalized);
            var blind = BlindHoleIntentRegex().IsMatch(normalized);
            var diameterMatch = HoleDiameterRegex().Match(normalized);

            if (blind)
                diagnostics.Add(new("NL023", DiagnosticSeverity.Error,
                    "Blind holes are not supported by the current executor and must not be converted to through holes.", "text",
                    "Use a centered through hole, or extend Modeling IR with an extrude_cut operation and blind depth."));
            else if (!through)
                diagnostics.Add(new("NL022", DiagnosticSeverity.Error,
                    "Hole depth is ambiguous. Specify through hole or blind-hole depth.", "text",
                    "Say '中心直径10毫米通孔' for the current supported workflow."));
            if (!centered)
                diagnostics.Add(new("NL021", DiagnosticSeverity.Error,
                    "Hole position is required; the compiler will not silently place an unspecified hole at the center.", "text",
                    "Specify that the hole is centered, or provide X/Y coordinates after extending the IR."));
            if (!diameterMatch.Success)
                diagnostics.Add(new("NL024", DiagnosticSeverity.Error,
                    "Hole diameter is required.", "text", "Specify the hole diameter in millimetres."));
            else if (centered && through && !blind)
            {
                var holeDiameter = Number(diameterMatch, "d");
                if (holeDiameter >= Math.Min(width, height))
                    diagnostics.Add(new("NL020", DiagnosticSeverity.Error,
                        "The requested hole diameter is not smaller than the plate envelope.", "text"));
                else
                    profiles.Add(new CircleProfile { Role = ContourRole.Inner, DiameterMm = holeDiameter });
            }
        }

        var sketch = new ProfileSketchOperation
        {
            Id = "sketch_base",
            Name = "Base_Profile",
            Plane = ReferencePlane.Front,
            Primitives = profiles
        };
        var extrude = new ExtrudeBossOperation
        {
            Id = "extrude_base",
            Name = "Base_Extrude",
            DependsOn = [sketch.Id],
            SketchId = sketch.Id,
            DepthMm = depth
        };
        return NewPlan("rectangular_plate", sourceText, output, assumptions, [sketch, extrude],
            new(width, height, depth), ProfileAreaMm2(sketch.Primitives) * depth);
    }

    private static ModelingPlan? TryCompileLChannel(
        string normalized,
        string sourceText,
        string? output,
        IReadOnlyList<string> assumptions,
        List<ModelingDiagnostic> diagnostics)
    {
        var envelope = LChannelEnvelopeRegex().Match(normalized);
        var left = LChannelLeftSectionRegex().Match(normalized);
        var transition = LChannelTransitionRegex().Match(normalized);
        var right = LChannelRightSectionRegex().Match(normalized);
        if (!envelope.Success || !left.Success || !transition.Success || !right.Success)
        {
            diagnostics.Add(new("NL120", DiagnosticSeverity.Error,
                "The variable L-channel intent was recognized, but its canonical dimensions are incomplete.", "text",
                "Specify outer envelope L×W×H, left length and rectangular opening W×H, transition length, and right length and rectangular opening W×H."));
            return null;
        }

        var length = Number(envelope, "l");
        var width = Number(envelope, "w");
        var height = Number(envelope, "h");
        var leftLength = Number(left, "len");
        var leftOpenA = Number(left, "a");
        var leftOpenB = Number(left, "b");
        var transitionLength = Number(transition, "len");
        var rightLength = Number(right, "len");
        var rightOpenA = Number(right, "a");
        var rightOpenB = Number(right, "b");

        var values = new[]
        {
            length, width, height, leftLength, leftOpenA, leftOpenB,
            transitionLength, rightLength, rightOpenA, rightOpenB
        };
        const double tolerance = 1e-6;
        if (values.Any(value => value <= 0 || !double.IsFinite(value)) ||
            Math.Abs(leftLength + transitionLength + rightLength - length) > tolerance ||
            leftOpenA >= width || rightOpenA >= width ||
            leftOpenB >= height || rightOpenB >= height)
        {
            diagnostics.Add(new("NL121", DiagnosticSeverity.Error,
                "The variable L-channel dimensions are geometrically inconsistent.", "text",
                "Require positive dimensions, opening widths smaller than the envelope width, opening heights smaller than the envelope height, and segment lengths summing to the total length."));
            return null;
        }

        var leftBaseThickness = height - leftOpenB;
        var rightBaseThickness = height - rightOpenB;
        var leftWallThickness = width - leftOpenA;
        var rightWallThickness = width - rightOpenA;
        var x0 = -length / 2d;
        var x1 = x0 + leftLength;
        var x2 = x1 + transitionLength;
        var x3 = length / 2d;

        static CompositeCurveProfile ClosedLineContour(params ProfilePoint[] points) => new()
        {
            Role = ContourRole.Outer,
            Curves = points.Select((point, index) => (ProfileCurve)new LineProfileCurve
            {
                Start = point,
                End = points[(index + 1) % points.Length]
            }).ToArray()
        };

        var baseSketch = new ProfileSketchOperation
        {
            Id = "sketch_variable_base_xz", Name = "Variable_Base_XZ_Profile", Plane = ReferencePlane.Front,
            Primitives =
            [
                ClosedLineContour(
                    new(x0, 0d), new(x3, 0d), new(x3, rightBaseThickness),
                    new(x2, rightBaseThickness), new(x1, leftBaseThickness), new(x0, leftBaseThickness))
            ]
        };
        var baseBoss = new ExtrudeBossOperation
        {
            Id = "boss_variable_base", Name = "Variable_Base_Boss", DependsOn = [baseSketch.Id],
            SketchId = baseSketch.Id, DepthMm = width, EndCondition = ExtrudeEndCondition.MidPlane
        };

        var wallOuterY = -width / 2d;
        var wallSketch = new ProfileSketchOperation
        {
            Id = "sketch_variable_wall_xy", Name = "Variable_Wall_XY_Profile", Plane = ReferencePlane.Top,
            DependsOn = [baseBoss.Id],
            Primitives =
            [
                ClosedLineContour(
                    new(x0, wallOuterY), new(x3, wallOuterY), new(x3, wallOuterY + rightWallThickness),
                    new(x2, wallOuterY + rightWallThickness), new(x1, wallOuterY + leftWallThickness),
                    new(x0, wallOuterY + leftWallThickness))
            ]
        };
        var wallBoss = new ExtrudeBossOperation
        {
            Id = "boss_variable_wall", Name = "Variable_Wall_Boss",
            DependsOn = [wallSketch.Id, baseBoss.Id], SketchId = wallSketch.Id,
            DepthMm = height, EndCondition = ExtrudeEndCondition.Blind, ReverseDirection = true
        };

        var transitionOpeningVolume = transitionLength / 6d *
            (2d * leftOpenA * leftOpenB + leftOpenA * rightOpenB +
             rightOpenA * leftOpenB + 2d * rightOpenA * rightOpenB);
        var removedVolume = leftLength * leftOpenA * leftOpenB +
                            transitionOpeningVolume +
                            rightLength * rightOpenA * rightOpenB;
        var expectedVolume = length * width * height - removedVolume;
        var explicitAssumptions = assumptions.ToList();
        explicitAssumptions.Add("A2. The rectangular opening remains anchored to one common outer corner; model orientation may be rotated or mirrored without changing geometry.");
        explicitAssumptions.Add("A3. Opening width and height change linearly over the transition, so the two L-leg thicknesses may vary independently; all unspecified edge breaks remain sharp.");

        diagnostics.Add(new("NL122", DiagnosticSeverity.Info,
            FormattableString.Invariant(
                $"Variable L-channel stations from the left datum: x=0 opening {leftOpenA}×{leftOpenB}; x={leftLength} transition start; x={leftLength + transitionLength} opening {rightOpenA}×{rightOpenB}; x={length} end. Derived left wall/base thicknesses are {leftWallThickness}/{leftBaseThickness}; right wall/base thicknesses are {rightWallThickness}/{rightBaseThickness}."),
            "text"));

        ModelingOperation[] operations = [baseSketch, baseBoss, wallSketch, wallBoss];
        var plan = NewPlan("variable_l_channel", sourceText, output, explicitAssumptions, operations,
            // This template sketches length × height on Front and extrudes width normal to it.
            // SolidWorks model-space bounding axes are therefore X=length, Y=height, Z=width.
            new(length, height, width), expectedVolume);
        return output is null
            ? plan
            : plan with { Output = plan.Output with { ExportPaths = [Path.ChangeExtension(output, ".step")] } };
    }

    private static ModelingPlan? TryCompileCylinder(
        string normalized,
        string sourceText,
        string? output,
        IReadOnlyList<string> assumptions,
        List<ModelingDiagnostic> diagnostics)
    {
        var match = CylinderRegex().Match(normalized);
        if (!match.Success) return null;
        var diameter = Number(match, "d");
        var height = Number(match, "h");
        if (diameter <= 0 || height <= 0) return null;

        var sketch = new ProfileSketchOperation
        {
            Id = "sketch_base",
            Name = "Base_Profile",
            Plane = ReferencePlane.Front,
            Primitives = [new CircleProfile { Role = ContourRole.Outer, DiameterMm = diameter }]
        };
        var extrude = new ExtrudeBossOperation
        {
            Id = "extrude_base",
            Name = "Base_Extrude",
            DependsOn = [sketch.Id],
            SketchId = sketch.Id,
            DepthMm = height
        };
        return NewPlan("cylinder", sourceText, output, assumptions, [sketch, extrude],
            new(diameter, diameter, height), ProfileAreaMm2(sketch.Primitives) * height);
    }

    private static ModelingPlan NewPlan(
        string name,
        string source,
        string? output,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<ModelingOperation> operations,
        BoundingBoxSpec bounds,
        double? expectedVolumeMm3 = null) => new()
    {
        PlanId = $"plan_{Guid.NewGuid():N}",
        Name = name,
        SourceText = source,
        Assumptions = assumptions,
        Operations = operations,
        Output = new() { NativePath = output, OverwriteAllowed = false },
        Acceptance = new()
        {
            ExpectedFeatures = operations.Select(x => x.Name).ToArray(),
            ExpectedBoundingBoxMm = bounds,
            Geometry = new() { ExpectedVolumeMm3 = expectedVolumeMm3 }
        }
    };

    private static double ProfileAreaMm2(IEnumerable<ProfilePrimitive> primitives) => primitives.Sum(primitive =>
    {
        var unsignedArea = primitive switch
        {
            CenteredRectangleProfile rectangle => rectangle.WidthMm * rectangle.HeightMm,
            CircleProfile circle => Math.PI * Math.Pow(circle.DiameterMm / 2d, 2d),
            PolygonProfile polygon => Math.Abs(polygon.Points.Select((point, index) =>
            {
                var next = polygon.Points[(index + 1) % polygon.Points.Count];
                return point.Xmm * next.Ymm - next.Xmm * point.Ymm;
            }).Sum()) / 2d,
            _ => 0d
        };
        return primitive.Role == ContourRole.Inner ? -unsignedArea : unsignedArea;
    });

    private static double Number(Match match, string group) =>
        double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static string Normalize(string text) =>
        text.Trim().ToLowerInvariant().Replace('×', 'x').Replace('＊', 'x').Replace(',', ' ');

    [GeneratedRegex(@"(?:mm|毫米|millimet(?:er|re)s?)", RegexOptions.IgnoreCase)]
    private static partial Regex MentionsUnitRegex();

    [GeneratedRegex(@"(?:双耳|叉形|叉耳|clevis|fork[\s-]*bracket)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkBracketIntentRegex();

    [GeneratedRegex(@"(?:变截面\s*l\s*形(?:开口|角形)?槽|变截面\s*(?:角槽|角形槽)|variable[\s-]*section.*l[\s-]*(?:channel|槽))", RegexOptions.IgnoreCase)]
    private static partial Regex LChannelIntentRegex();

    [GeneratedRegex(@"(?:外包络|外形|envelope)\s*(?<l>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<w>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<h>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex LChannelEnvelopeRegex();

    [GeneratedRegex(@"(?:左段|起始段)(?:长度)?\s*(?<len>\d+(?:\.\d+)?)\s*(?:mm|毫米)?.{0,32}?(?:(?:内部)?(?:方形|矩形)?开口)\s*(?<a>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<b>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex LChannelLeftSectionRegex();

    [GeneratedRegex(@"过渡段(?:长度)?\s*(?<len>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex LChannelTransitionRegex();

    [GeneratedRegex(@"(?:右段|末段)(?:长度)?\s*(?<len>\d+(?:\.\d+)?)\s*(?:mm|毫米)?.{0,32}?(?:(?:内部)?(?:方形|矩形)?开口)\s*(?<a>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<b>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex LChannelRightSectionRegex();

    [GeneratedRegex(@"(?:底座|base)\s*(?:尺寸)?\s*(?<w>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<d>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<t>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex ForkBaseDimensionsRegex();

    [GeneratedRegex(@"(?:安装孔|底座孔).{0,20}?(?:直径|[ø⌀])\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkMountingHoleDiameterRegex();

    [GeneratedRegex(@"(?:安装孔|底座孔).{0,30}?(?:中心距|孔距)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkMountingHoleSpacingRegex();

    [GeneratedRegex(@"(?:双耳外宽|耳板外宽|耳外宽)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkEarOuterWidthRegex();

    [GeneratedRegex(@"(?:内间距|内距|槽宽)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkEarGapRegex();

    [GeneratedRegex(@"(?:耳孔|销孔).{0,12}?(?:直径|[ø⌀])\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkPinHoleDiameterRegex();

    [GeneratedRegex(@"(?:孔心距底面|销孔中心距底面)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkPinCenterHeightRegex();

    [GeneratedRegex(@"(?:圆头\s*)?r\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkHeadRadiusRegex();

    [GeneratedRegex(@"(?:孔心到耳片右端|孔心到右端|孔心距耳片右端)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkPinToEarEndRegex();

    [GeneratedRegex(@"(?:台阶距底座右端|台阶到底座右端|台阶距右端)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkStepToBaseEndRegex();

    [GeneratedRegex(@"(?:右端高度|耳片右端高度|末端高度)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkEarEndHeightRegex();

    [GeneratedRegex(@"(?:长(?:度)?\s*)?(?<x>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*(?:宽(?:度)?\s*)(?<y>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*(?:厚(?:度)?|高(?:度)?)\s*(?<z>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChinesePlateRegex();

    [GeneratedRegex(@"(?<x>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<y>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<z>\d+(?:\.\d+)?)\s*(?:mm|毫米)?(?:\s*(?:plate|板|块|rectangular))?", RegexOptions.IgnoreCase)]
    private static partial Regex DimensionTripletRegex();

    [GeneratedRegex(@"(?:孔|hole)", RegexOptions.IgnoreCase)]
    private static partial Regex HoleIntentRegex();

    [GeneratedRegex(@"(?:中心|中央|center(?:ed)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CenterIntentRegex();

    [GeneratedRegex(@"(?:通孔|through(?:[\s-]*hole)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ThroughHoleIntentRegex();

    [GeneratedRegex(@"(?:盲孔|blind(?:[\s-]*hole)?)", RegexOptions.IgnoreCase)]
    private static partial Regex BlindHoleIntentRegex();

    [GeneratedRegex(@"(?:直径|diameter|[ø⌀])\s*(?<d>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex HoleDiameterRegex();

    [GeneratedRegex(@"(?:圆柱|cylinder).{0,20}?(?:直径|diameter|[ø⌀])\s*(?<d>\d+(?:\.\d+)?)\s*(?:mm|毫米)?.{0,20}?(?:高(?:度)?|height|长(?:度)?)\s*(?<h>\d+(?:\.\d+)?)|(?:直径|diameter|[ø⌀])\s*(?<d>\d+(?:\.\d+)?)\s*(?:mm|毫米)?.{0,20}?(?:高(?:度)?|height|长(?:度)?)\s*(?<h>\d+(?:\.\d+)?)\s*(?:mm|毫米)?.{0,12}?(?:圆柱|cylinder)", RegexOptions.IgnoreCase)]
    private static partial Regex CylinderRegex();
}

public sealed class ModelingWorkflow(
    RuleBasedTextCompiler compiler,
    ModelingIrValidator validator,
    IModelingExecutor executor)
{
    public CompilationResult Compile(string text, string? nativeOutputPath = null) => compiler.Compile(text, nativeOutputPath);
    public ValidationReport Validate(ModelingPlan plan, bool forExecution = false) => validator.Validate(plan, forExecution);
    public Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default) => executor.HealthAsync(cancellationToken);

    public async Task<ExecutionResult> ExecuteAsync(ModelingPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        var report = validator.Validate(plan, forExecution: true);
        if (!report.IsValid)
            return new(false, "rejected", "Modeling IR validation failed.",
                report.Diagnostics.Select(x => new ExecutionEvidence(
                    "validation", $"{x.Code}: {x.Message}", false, Code: x.Code,
                    Category: ExecutionFailureCategory.Validation,
                    SuggestedAction: x.SuggestedAction)).ToArray(),
                PlanFingerprint: ModelingPlanIdentity.Fingerprint(plan));
        return await executor.ExecuteAsync(plan, dryRun, cancellationToken);
    }
}

public sealed class MockModelingExecutor : IModelingExecutor
{
    public Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ExecutorHealth(true, "mock", "Mock executor is available."));

    public Task<ExecutionResult> ExecuteAsync(ModelingPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        var evidence = plan.Operations.Select(x =>
            new ExecutionEvidence("operation", $"Validated {x.Id} ({x.GetType().Name}).", true,
                new Dictionary<string, string> { ["name"] = x.Name })).ToArray();
        return Task.FromResult(new ExecutionResult(true, dryRun ? "dry_run" : "mocked", "IR accepted by mock executor.",
            evidence, plan.Output.NativePath, PlanFingerprint: ModelingPlanIdentity.Fingerprint(plan)));
    }
}

public static class ModelingPlanIdentity
{
    public static string Fingerprint(ModelingPlan plan)
    {
        var canonical = ModelingIrJson.Serialize(plan, indented: false);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record ExecutorServiceRequest(string Action, ModelingPlan? Plan = null, bool DryRun = false, ModelInspectionRequest? Inspection = null,AssemblyPlan? Assembly = null);
public sealed record ExecutorServiceResponse(ExecutorHealth? Health = null, ExecutionResult? Execution = null, string? Error = null, ModelInspection? Inspection = null,AssemblyResult? Assembly = null);

public sealed class NamedPipeModelingExecutor(
    string pipeName = "cad-modeling-solidworks",
    int connectTimeoutMilliseconds = 5000) : IModelingExecutor
{
    public async Task<AssemblyResult> BuildAssemblyAsync(AssemblyPlan plan,CancellationToken cancellationToken=default)
    {
        var response=await SendAsync(new("assembly",Assembly:plan),cancellationToken);
        return response.Assembly??new(false,response.Error??"No assembly response.");
    }
    public async Task<ModelInspection> InspectAsync(ModelInspectionRequest request, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new("inspect", Inspection: request), cancellationToken);
        return response.Inspection ?? new(false, response.Error ?? "No inspection response.", request.InputPath);
    }
    public async Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new("health"), cancellationToken);
        return response.Health ?? new(false, "named-pipe", response.Error ?? "Executor returned no health result.");
    }

    public async Task<ExecutionResult> ExecuteAsync(ModelingPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new("execute", plan, dryRun), cancellationToken);
        return response.Execution ?? new(false, "failed", response.Error ?? "Executor returned no execution result.", []);
    }

    private async Task<ExecutorServiceResponse> SendAsync(ExecutorServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectTimeoutMilliseconds, cancellationToken);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ModelingIrJson.Options));
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) return new(Error: "Executor closed the pipe without a response.");
            return JsonSerializer.Deserialize<ExecutorServiceResponse>(line, ModelingIrJson.Options)
                   ?? new(Error: "Executor response was empty.");
        }
        catch (TimeoutException)
        {
            return new(Error: $"Timed out connecting to SolidWorks executor pipe '{pipeName}'. Start the executor service first.");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return new(Error: $"Could not communicate with SolidWorks executor: {ex.Message}");
        }
    }
}

public sealed record LocalExecutorLaunchOptions
{
    public string PipeName { get; init; } = "cad-modeling-solidworks";
    public string? ExecutablePath { get; init; }
    public string? LogDirectory { get; init; }
    public int StartupTimeoutMilliseconds { get; init; } = 20000;
}

public sealed class AutoStartingNamedPipeModelingExecutor : IModelingExecutor, IDisposable
{
    public async Task<AssemblyResult> BuildAssemblyAsync(AssemblyPlan plan,CancellationToken cancellationToken=default)
    {
        var health=await HealthAsync(cancellationToken);
        return health.Available?await _client.BuildAssemblyAsync(plan,cancellationToken):new(false,health.Message);
    }
    public async Task<ModelInspection> InspectAsync(ModelInspectionRequest request, CancellationToken cancellationToken = default)
    {
        var health = await HealthAsync(cancellationToken);
        return health.Available ? await _client.InspectAsync(request, cancellationToken) : new(false, health.Message, request.InputPath);
    }
    private readonly LocalExecutorLaunchOptions _options;
    private readonly NamedPipeModelingExecutor _client;
    private readonly NamedPipeModelingExecutor _fastProbe;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _logGate = new();
    private Process? _ownedProcess;
    private StreamWriter? _logWriter;
    private bool _disposed;

    public AutoStartingNamedPipeModelingExecutor(LocalExecutorLaunchOptions options)
    {
        _options = options;
        _client = new(options.PipeName);
        _fastProbe = new(options.PipeName, connectTimeoutMilliseconds: 250);
    }

    public async Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _fastProbe.HealthAsync(cancellationToken);
        if (existing.Available) return existing;

        var launchError = await EnsureServiceStartedAsync(cancellationToken);
        if (launchError is not null)
            return new(false, "solidworks-com-service", launchError);

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(_options.StartupTimeoutMilliseconds);
        ExecutorHealth last = existing;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_ownedProcess is { HasExited: true })
                return new(false, "solidworks-com-service",
                    $"SolidWorks executor exited during startup with code {_ownedProcess.ExitCode}. See '{CurrentLogPath()}'.");

            last = await _fastProbe.HealthAsync(cancellationToken);
            if (last.Available) return last;
            await Task.Delay(150, cancellationToken);
        }

        return new(false, "solidworks-com-service",
            $"SolidWorks executor did not become ready within {_options.StartupTimeoutMilliseconds} ms. " +
            $"Last result: {last.Message} See '{CurrentLogPath()}'.");
    }

    public async Task<ExecutionResult> ExecuteAsync(
        ModelingPlan plan,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var health = await HealthAsync(cancellationToken);
        if (!health.Available)
            return new(false, "executor_unavailable", health.Message,
                [new("executor", health.Message, false)]);
        var result = await _client.ExecuteAsync(plan, dryRun, cancellationToken);
        var healthData = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(health.SolidWorksRevision))
            healthData["solidworks_revision"] = health.SolidWorksRevision;
        if (!string.IsNullOrWhiteSpace(health.ActiveDocument))
            healthData["active_document"] = health.ActiveDocument;
        return result with
        {
            Evidence =
            [
                new ExecutionEvidence("executor_health", health.Message, true,
                    healthData.Count == 0 ? null : healthData),
                .. result.Evidence
            ]
        };
    }

    private async Task<string?> EnsureServiceStartedAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) return "Executor client has already been disposed.";
            if (_ownedProcess is { HasExited: false }) return null;

            var existing = await _fastProbe.HealthAsync(cancellationToken);
            if (existing.Available) return null;

            var executable = ResolveExecutorPath(_options.ExecutablePath);
            if (executable is null)
                return "Could not locate CadModeling.Executor.SolidWorks.exe. Set CAD_SOLIDWORKS_EXECUTOR to its absolute path.";

            var logDirectory = Path.GetFullPath(_options.LogDirectory ??
                Path.Combine(Path.GetTempPath(), "SolidWorksAgent", "logs"));
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, $"executor-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            _logWriter = new StreamWriter(logPath, append: true, new UTF8Encoding(false)) { AutoFlush = true };

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("serve");
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(_options.PipeName);
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            _ownedProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _ownedProcess.OutputDataReceived += (_, eventArgs) => WriteLog("stdout", eventArgs.Data);
            _ownedProcess.ErrorDataReceived += (_, eventArgs) => WriteLog("stderr", eventArgs.Data);
            if (!_ownedProcess.Start()) return $"Failed to start SolidWorks executor '{executable}'.";
            _ownedProcess.BeginOutputReadLine();
            _ownedProcess.BeginErrorReadLine();
            WriteLog("host", $"Started executor pid={_ownedProcess.Id}, pipe={_options.PipeName}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"Could not start the SolidWorks executor: {ex.Message}";
        }
        finally
        {
            _startGate.Release();
        }
    }

    public static string? ResolveExecutorPath(string? configuredPath = null)
    {
        configuredPath ??= Environment.GetEnvironmentVariable("CAD_SOLIDWORKS_EXECUTOR");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
            return File.Exists(full) ? full : null;
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "CadModeling.Executor.SolidWorks.exe")
        };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "src", "CadModeling.Executor.SolidWorks", "bin", "Debug", "net9.0-windows", "CadModeling.Executor.SolidWorks.exe"));
            candidates.Add(Path.Combine(directory.FullName, "src", "CadModeling.Executor.SolidWorks", "bin", "Release", "net9.0-windows", "CadModeling.Executor.SolidWorks.exe"));
        }
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private string CurrentLogPath() => _logWriter is null
        ? _options.LogDirectory ?? Path.Combine(Path.GetTempPath(), "SolidWorksAgent", "logs")
        : ((FileStream)_logWriter.BaseStream).Name;

    private void WriteLog(string source, string? message)
    {
        if (message is null) return;
        lock (_logGate)
            _logWriter?.WriteLine($"{DateTimeOffset.Now:O} [{source}] {message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_ownedProcess is { HasExited: false })
            {
                _ownedProcess.Kill(entireProcessTree: false);
                _ownedProcess.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            _ownedProcess?.Dispose();
            _logWriter?.Dispose();
            _startGate.Dispose();
        }
    }
}
