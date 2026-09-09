using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CadModeling.Ir;

public static class ModelingIrSchema
{
    public const string CurrentVersion = "2.0";
    public static IReadOnlySet<string> SupportedVersions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "1.1", "1.2", "1.3", "1.4", CurrentVersion };

    public static bool IsSupported(string version) => SupportedVersions.Contains(version);
}

[JsonConverter(typeof(JsonStringEnumConverter<DocumentKind>))]
public enum DocumentKind { Part, Assembly, Drawing }

[JsonConverter(typeof(JsonStringEnumConverter<LengthUnit>))]
public enum LengthUnit { Millimeter }

[JsonConverter(typeof(JsonStringEnumConverter<ReferencePlane>))]
public enum ReferencePlane { Front, Top, Right }

[JsonConverter(typeof(JsonStringEnumConverter<ContourRole>))]
public enum ContourRole { Outer, Inner }

[JsonConverter(typeof(JsonStringEnumConverter<ExtrudeEndCondition>))]
public enum ExtrudeEndCondition { Blind, MidPlane, UpToSurface, ThroughAll, UpToNext }

public sealed record ModelingPlan
{
    public ModelVerificationSpec Verification { get; init; } = new();
    public ModelingRecoveryOptions Recovery { get; init; } = new();
    public string? DrawingBindingDigest { get; init; }
    public string? DrawingSourceSha256 { get; init; }
    public DrawingPlanContext? DrawingContext { get; init; }
    public string SchemaVersion { get; init; } = ModelingIrSchema.CurrentVersion;
    public required string PlanId { get; init; }
    public required string Name { get; init; }
    public DocumentKind DocumentKind { get; init; } = DocumentKind.Part;
    public LengthUnit LengthUnit { get; init; } = LengthUnit.Millimeter;
    public string SourceText { get; init; } = string.Empty;
    public string? SourceModelPath { get; init; }
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public required IReadOnlyList<ModelingOperation> Operations { get; init; }
    public OutputSpec Output { get; init; } = new();
    public AcceptanceSpec Acceptance { get; init; } = new();
}

public sealed record OutputSpec
{
    public string? NativePath { get; init; }
    public IReadOnlyList<string> ExportPaths { get; init; } = [];
    public bool OverwriteAllowed { get; init; }
}

public sealed record AcceptanceSpec
{
    public bool RebuildWithoutErrors { get; init; } = true;
    public IReadOnlyList<string> ExpectedFeatures { get; init; } = [];
    public BoundingBoxSpec? ExpectedBoundingBoxMm { get; init; }
    public double BoundingBoxToleranceMm { get; init; } = 0.1;
    public GeometryQualitySpec Geometry { get; init; } = new();
}

public sealed record BoundingBoxSpec(double X, double Y, double Z);

public sealed record GeometryQualitySpec
{
    public Vector3? ExpectedCenterOfMassMm { get; init; }
    public double CenterOfMassToleranceMm { get; init; } = 0.1;
    public int? ExpectedSurfaceBodyCount { get; init; }
    public int ExpectedSolidBodyCount { get; init; } = 1;
    public bool RequirePositiveVolume { get; init; } = true;
    public bool RequireValidTopology { get; init; } = true;
    public double? ExpectedVolumeMm3 { get; init; }
    public double VolumeTolerancePercent { get; init; } = 0.5;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProfileSketchOperation), "profile_sketch")]
[JsonDerivedType(typeof(ExtrudeBossOperation), "extrude_boss")]
[JsonDerivedType(typeof(ExtrudeCutOperation), "extrude_cut")]
[JsonDerivedType(typeof(NativeFeatureOperation), "native_feature")]
public abstract record ModelingOperation
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

public sealed record ProfileSketchOperation : ModelingOperation
{
    public IReadOnlyList<SketchConstraintSpec> Constraints { get; init; } = [];
    public IReadOnlyList<SketchDimensionSpec> Dimensions { get; init; } = [];
    public IReadOnlyList<SketchEditSpec> Edits { get; init; } = [];
    public SketchFrame? Frame { get; init; }
    public string? PlaneId { get; init; }
    public ReferencePlane Plane { get; init; } = ReferencePlane.Front;
    public PlanarFaceAttachment? FaceAttachment { get; init; }
    public required IReadOnlyList<ProfilePrimitive> Primitives { get; init; }
}

/// <summary>
/// Places a sketch on a planar face at a deterministic model-space pick point.
/// The referenced feature must already exist and be listed in the sketch dependencies.
/// </summary>
public sealed record PlanarFaceAttachment
{
    public required string SupportOperationId { get; init; }
    public required double PickXmm { get; init; }
    public required double PickYmm { get; init; }
    public required double PickZmm { get; init; }
}

public sealed record ExtrudeBossOperation : ModelingOperation
{
    public bool Merge { get; init; } = true;
    public required string SketchId { get; init; }
    public double DepthMm { get; init; }
    public ExtrudeEndCondition EndCondition { get; init; } = ExtrudeEndCondition.Blind;
    public PlanarFaceReference? EndReference { get; init; }
    public double StartOffsetMm { get; init; }
    public bool ReverseStartOffset { get; init; }
    public bool ReverseDirection { get; init; }
}

public sealed record ExtrudeCutOperation : ModelingOperation
{
    public required string SketchId { get; init; }
    public double DepthMm { get; init; }
    public ExtrudeEndCondition EndCondition { get; init; } = ExtrudeEndCondition.Blind;
    public PlanarFaceReference? EndReference { get; init; }
    public double StartOffsetMm { get; init; }
    public bool ReverseStartOffset { get; init; }
    public bool ReverseDirection { get; init; }
}

/// <summary>
/// Selects a deterministic planar face as an extrusion termination reference.
/// The referenced feature must already exist and be listed in the extrusion dependencies.
/// </summary>
public sealed record PlanarFaceReference
{
    public required string SupportOperationId { get; init; }
    public required double PickXmm { get; init; }
    public required double PickYmm { get; init; }
    public required double PickZmm { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CenteredRectangleProfile), "centered_rectangle")]
[JsonDerivedType(typeof(ThreePointRectangleProfile), "three_point_rectangle")]
[JsonDerivedType(typeof(CircleProfile), "circle")]
[JsonDerivedType(typeof(PolygonProfile), "polygon")]
[JsonDerivedType(typeof(CompositeCurveProfile), "composite_curve")]
[JsonDerivedType(typeof(EllipseProfile), "ellipse")]
[JsonDerivedType(typeof(OpenCurveProfile), "open_curve")]
[JsonDerivedType(typeof(SplineProfile), "spline")]
[JsonDerivedType(typeof(SketchPointsProfile), "points")]
public abstract record ProfilePrimitive
{
    public ContourRole Role { get; init; } = ContourRole.Outer;
    public double CenterXmm { get; init; }
    public double CenterYmm { get; init; }
}

public sealed record CenteredRectangleProfile : ProfilePrimitive
{
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }
}

public sealed record ThreePointRectangleProfile : ProfilePrimitive
{
    public required ProfilePoint Corner1 { get; init; }
    public required ProfilePoint Corner2 { get; init; }
    public required ProfilePoint Corner3 { get; init; }
}

public sealed record CircleProfile : ProfilePrimitive
{
    public required double DiameterMm { get; init; }
}

public sealed record ProfilePoint(double Xmm, double Ymm);

public sealed record PolygonProfile : ProfilePrimitive
{
    public required IReadOnlyList<ProfilePoint> Points { get; init; }
}

/// <summary>
/// A single closed contour made of connected straight lines and three-point arcs.
/// It keeps the user-facing sketch construction order instead of approximating an arc with a polygon.
/// </summary>
public sealed record CompositeCurveProfile : ProfilePrimitive
{
    public required IReadOnlyList<ProfileCurve> Curves { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LineProfileCurve), "line")]
[JsonDerivedType(typeof(ThreePointArcProfileCurve), "three_point_arc")]
public abstract record ProfileCurve
{
    public required ProfilePoint Start { get; init; }
    public required ProfilePoint End { get; init; }
}

public sealed record LineProfileCurve : ProfileCurve;

public sealed record ThreePointArcProfileCurve : ProfileCurve
{
    public required ProfilePoint PointOnArc { get; init; }
}

public static class ModelingIrJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(ModelingPlan plan, bool indented = true)
    {
        var options = new JsonSerializerOptions(Options) { WriteIndented = indented };
        return JsonSerializer.Serialize(plan, options);
    }

    public static ModelingPlan Deserialize(string json) =>
        JsonSerializer.Deserialize<ModelingPlan>(json, Options)
        ?? throw new JsonException("Modeling IR document was empty.");

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}
