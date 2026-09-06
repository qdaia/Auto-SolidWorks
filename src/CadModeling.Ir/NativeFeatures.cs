using System.Text.Json.Serialization;
namespace CadModeling.Ir;

public sealed record Vector3(double X, double Y, double Z);
public sealed record SketchFrame
{
    public Vector3 OriginMm { get; init; } = new(0, 0, 0);
    public Vector3 XDirection { get; init; } = new(1, 0, 0);
    public Vector3 Normal { get; init; } = new(0, 0, 1);
}

[JsonConverter(typeof(JsonStringEnumConverter<EntityKind>))]
public enum EntityKind { Feature, Face, Edge, Body, Plane, Axis }
[JsonConverter(typeof(JsonStringEnumConverter<GeometryKind>))]
public enum GeometryKind { Any, Plane, Cylinder, Cone, Sphere, Torus, Line, Circle }

/// <summary>Geometry-based selection. Position is a point on the entity, in model mm.
/// Multiple matches require AllMatches=true; no arbitrary first-edge fallback.</summary>
public sealed record EntityQuery
{
    public EntityKind Kind { get; init; } = EntityKind.Edge;
    public string? FeatureId { get; init; }
    public string? Name { get; init; }
    public string? PersistentReference { get; init; }
    public GeometryKind Geometry { get; init; } = GeometryKind.Any;
    public Vector3? PositionMm { get; init; }
    public Vector3? Direction { get; init; }
    public double? RadiusMm { get; init; }
    public double ToleranceMm { get; init; } = 0.05;
    public bool AllMatches { get; init; }
    public int SelectionMark { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<NativeFeatureKind>))]
public enum NativeFeatureKind
{
    ReferencePlane, ReferenceAxis, Chamfer, Fillet, RevolveBoss, RevolveCut,
    Shell, Draft, Mirror, LinearPattern, CircularPattern, Combine, MoveBody,
    LoftBoss, LoftCut, SweepBoss, SweepCut, Rib, Hole,
    SheetMetalBase, EdgeFlange, Flatten, WeldmentMember, TrimWeldment,
    SurfaceExtrude, SurfaceLoft, SurfaceTrim, SurfaceKnit, Thicken,
    SetDimension, Suppress, Restore, Split, ThinExtrude, SurfacePlanar, SketchPattern
}

[JsonConverter(typeof(JsonStringEnumConverter<ChamferMode>))]
public enum ChamferMode { DistanceAngle, TwoDistances, EqualDistance }
[JsonConverter(typeof(JsonStringEnumConverter<BooleanMode>))]
public enum BooleanMode { Union, Subtract, Intersect }
[JsonConverter(typeof(JsonStringEnumConverter<HoleKind>))]
public enum HoleKind { Simple, Counterbore, Countersink, Tapped }
[JsonConverter(typeof(JsonStringEnumConverter<WeldmentEndCondition>))]
public enum WeldmentEndCondition { Miter=1, Butt1=2, Butt2=3, Trim=4 }

public sealed record NativeFeatureOptions
{
    public NativeFeatureKind Kind { get; init; }
    public IReadOnlyList<EntityQuery> Selections { get; init; } = [];
    public SketchFrame? Frame { get; init; }
    public string? SketchId { get; init; }
    public string? PathSketchId { get; init; }
    public IReadOnlyList<string> ProfileIds { get; init; } = [];
    public IReadOnlyList<string> GuideIds { get; init; } = [];
    public string? AxisId { get; init; }
    public Vector3? AxisStartMm { get; init; }
    public Vector3? AxisEndMm { get; init; }
    public double AngleDegrees { get; init; } = 360;
    public bool Reverse { get; init; }
    public bool Merge { get; init; } = true;
    public bool TryToFormSolid { get; init; }
    public bool TangentPropagation { get; init; } = true;
    public double RadiusMm { get; init; }
    public double DistanceMm { get; init; }
    public double SecondDistanceMm { get; init; }
    public double ThicknessMm { get; init; }
    public double CounterboreDiameterMm { get; init; }
    public double CounterboreDepthMm { get; init; }
    public double CountersinkDiameterMm { get; init; }
    public double CountersinkAngleDegrees { get; init; } = 90;
    public double ThreadMajorDiameterMm { get; init; }
    public ChamferMode ChamferMode { get; init; } = ChamferMode.DistanceAngle;
    public int Count { get; init; } = 2;
    public double SpacingMm { get; init; }
    public BooleanMode BooleanMode { get; init; }
    public Vector3 TranslationMm { get; init; } = new(0, 0, 0);
    public Vector3 RotationDegrees { get; init; } = new(0, 0, 0);
    public bool Copy { get; init; }
    public HoleKind HoleKind { get; init; }
    public double DiameterMm { get; init; }
    public double DepthMm { get; init; }
    public bool ThroughAll { get; init; } = true;
    public IReadOnlyList<ProfilePoint> HoleCenters { get; init; } = [];
    public string? ThreadDesignation { get; init; }
    public string? ProfilePath { get; init; }
    public string? ProfileConfiguration { get; init; }
    public WeldmentEndCondition WeldmentEndCondition { get; init; } = WeldmentEndCondition.Trim;
    public double KFactor { get; init; } = 0.5;
    public bool Flattened { get; init; } = true;
    public string? DimensionName { get; init; }
    public double DimensionValue { get; init; }
    public bool DimensionIsAngle { get; init; }
}

public sealed record NativeFeatureOperation : ModelingOperation
{
    public required NativeFeatureOptions Options { get; init; }
}

public sealed record EllipseProfile : ProfilePrimitive
{
    public double MajorRadiusMm { get; init; }
    public double MinorRadiusMm { get; init; }
}
public sealed record OpenCurveProfile : ProfilePrimitive
{
    public IReadOnlyList<ProfileCurve> Curves { get; init; } = [];
    public bool Construction { get; init; }
}
public sealed record SplineProfile : ProfilePrimitive
{
    public IReadOnlyList<ProfilePoint> Points { get; init; } = [];
    public bool Closed { get; init; }
}
public sealed record SketchPointsProfile : ProfilePrimitive
{
    public IReadOnlyList<ProfilePoint> Points { get; init; } = [];
}
