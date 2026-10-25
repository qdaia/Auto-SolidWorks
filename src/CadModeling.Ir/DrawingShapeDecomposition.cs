using System.Text.Json.Serialization;
namespace CadModeling.Ir;

[JsonConverter(typeof(JsonStringEnumConverter<DrawingShapeKind>))]
public enum DrawingShapeKind { General, Prismatic, AxialAngular }
[JsonConverter(typeof(JsonStringEnumConverter<DrawingContourRole>))]
public enum DrawingContourRole { OuterBoundary, InnerBoundary, Step, Opening, Interface, HiddenBoundary, ReferenceOnly }
[JsonConverter(typeof(JsonStringEnumConverter<DrawingMaterialState>))]
public enum DrawingMaterialState { Material, Void }

/// <summary>AI-authored source contour, before modeling. This is not an automatic edge detector.</summary>
public sealed record DrawingShapeContour
{
    public required string Id { get; init; }
    public required string ViewId { get; init; }
    public required DrawingReviewBox Bounds { get; init; }
    public DrawingContourRole Role { get; init; }
    public IReadOnlyList<string> RegionIds { get; init; } = [];
    public required string Evidence { get; init; }
    public string? NonMaterialReason { get; init; }
}

/// <summary>Expected source material or void; operations may realize voids by a profile or by a cut.</summary>
public sealed record DrawingMaterialRegion
{
    /// <summary>Source-authored interior segments for Prismatic/General regions or supplementary axial checks; never copied from the resulting model.</summary>
    public IReadOnlyList<SourceMaterialSegment> ProbeSegments { get; init; } = [];
    public required string Id { get; init; }
    public DrawingMaterialState MaterialState { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> SourceContourIds { get; init; } = [];
    public IReadOnlyList<string> OperationIds { get; init; } = [];
    public IReadOnlyList<string> VerificationCheckIds { get; init; } = [];
    public required string Realization { get; init; }
}

public sealed record DrawingAngularSector
{
    public required double StartDegrees { get; init; }
    public required double EndDegrees { get; init; }
    public required string RegionId { get; init; }
}

public sealed record DrawingAxialLayer
{
    public required string Id { get; init; }
    public required double MinimumMm { get; init; }
    public required double MaximumMm { get; init; }
    public required string BoundaryEvidence { get; init; }
    /// <summary>Explicit material AND void sectors partition [0,360]. Split sectors crossing zero.</summary>
    public IReadOnlyList<DrawingAngularSector> Sectors { get; init; } = [];
}

/// <summary>A source-derived radial band. Multiple radial bands can describe a stepped shaft/bore.</summary>
public sealed record DrawingAxialMaterialMap
{
    public required string Id { get; init; }
    public required Vector3 OriginMm { get; init; }
    public required Vector3 AxisDirection { get; init; }
    public required Vector3 ZeroAngleDirection { get; init; }
    public required double InnerRadiusMm { get; init; }
    public required double OuterRadiusMm { get; init; }
    public required double MinimumMm { get; init; }
    public required double MaximumMm { get; init; }
    public required string DatumEvidence { get; init; }
    public IReadOnlyList<DrawingAxialLayer> Layers { get; init; } = [];
}

public sealed record DrawingFeatureDecomposition
{
    public required string FeatureId { get; init; }
    public DrawingShapeKind Kind { get; init; }
    public DrawingReviewState State { get; init; }
    /// <summary>Explain local additions/removals and what evidence rules out alternative topology.</summary>
    public required string TopologyReview { get; init; }
    public IReadOnlyList<DrawingShapeContour> Contours { get; init; } = [];
    public IReadOnlyList<DrawingMaterialRegion> Regions { get; init; } = [];
    public IReadOnlyList<DrawingAxialMaterialMap> AxialMaps { get; init; } = [];
}

public sealed record DrawingShapeDecomposition
{
    public string Revision { get; init; } = "shape-decomposition-1";
    public DrawingReviewState State { get; init; }
    public IReadOnlyList<DrawingFeatureDecomposition> Features { get; init; } = [];
}
