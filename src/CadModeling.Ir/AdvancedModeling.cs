using System.Text.Json.Serialization;
namespace CadModeling.Ir;

[JsonConverter(typeof(JsonStringEnumConverter<SpatialCurveKind>))]
public enum SpatialCurveKind { Polyline, InterpolatingSpline }
/// <summary>Explicit model-space points, mm. Does not infer design intent or silently project to a plane.</summary>
public sealed record SpatialCurveOptions
{
    public SpatialCurveKind CurveKind { get; init; } = SpatialCurveKind.InterpolatingSpline;
    public IReadOnlyList<Vector3> PointsMm { get; init; } = [];
    public bool Closed { get; init; }
    public bool NaturalEnds { get; init; } = true;
}
/// <summary>Constant-pitch cylindrical helix from one circle sketch; pitch in mm, starting angle in degrees.</summary>
public sealed record HelixOptions
{
    public double PitchMm { get; init; }
    public double Revolutions { get; init; }
    public double StartAngleDegrees { get; init; }
    public bool Clockwise { get; init; } = true;
}
public sealed record LinearPatternOptions
{
    public int SecondCount { get; init; } = 1;
    public double SecondSpacingMm { get; init; }
    public bool ReverseSecondDirection { get; init; }
    public bool GeometryPattern { get; init; } = true;
    public bool SecondDirectionSeedOnly { get; init; }
}
[JsonConverter(typeof(JsonStringEnumConverter<SweepOrientation>))]
public enum SweepOrientation { FollowPath, KeepNormalConstant, FollowFirstGuide, FollowTwoGuides, TwistAlongPath, TwistWithConstantNormal }
public sealed record SweepOptions
{
    public SweepOrientation Orientation { get; init; }
    public double TwistAngleDegrees { get; init; }
    public bool KeepTangency { get; init; } = true;
    public bool AdvancedSmoothing { get; init; } = true;
    public bool MergeSmoothFaces { get; init; } = true;
}
public sealed record LoftOptions
{
    /// <summary>Earlier sketch/spatial curve selected with native centerline mark 4.</summary>
    public string? CenterlineId { get; init; }
    public LoftGuideInfluence? GuideInfluence { get; init; }
    public double? StartTangentLengthMm { get; init; }
    public double? EndTangentLengthMm { get; init; }
    public bool ReverseStartTangent { get; init; }
    public bool ReverseEndTangent { get; init; }
    public bool MaintainTangency { get; init; } = true;
    public bool Close { get; init; }
    public SurfaceEndCondition StartCondition { get; init; }
    public SurfaceEndCondition EndCondition { get; init; }
}
[JsonConverter(typeof(JsonStringEnumConverter<LoftGuideInfluence>))]
public enum LoftGuideInfluence { NextGuide = 0, NextSharp = 1, NextEdge = 2, Global = 3 }
/// <summary>One open edge per query. Endpoint radii use native curve start/end order, checked against declared model points.</summary>
public sealed record VariableFilletEdge
{
    public required EntityQuery Edge { get; init; }
    public required Vector3 StartPointMm { get; init; }
    public required Vector3 EndPointMm { get; init; }
    public double StartRadiusMm { get; init; }
    public double EndRadiusMm { get; init; }
}
public sealed record VariableFilletOptions
{
    public IReadOnlyList<VariableFilletEdge> Edges { get; init; } = [];
    public bool CurvatureContinuous { get; init; }
}
[JsonConverter(typeof(JsonStringEnumConverter<SurfaceContact>))]
public enum SurfaceContact { Contact, Tangent, Curvature }
/// <summary>One native trimmed edge and, for G1/G2, one adjacent support face. Parallel ordered arrays reach the API.</summary>
public sealed record FillBoundaryConstraint
{
    public required EntityQuery Edge { get; init; }
    public SurfaceContact Contact { get; init; }
    public EntityQuery? SupportFace { get; init; }
}
