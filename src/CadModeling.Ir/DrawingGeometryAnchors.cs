using System.Text.Json.Serialization;
namespace CadModeling.Ir;

[JsonConverter(typeof(JsonStringEnumConverter<DrawingAnchorKind>))]
public enum DrawingAnchorKind { Point, Axis, Line, Plane, Circle, Arc, Contour }

[JsonConverter(typeof(JsonStringEnumConverter<DrawingAnchorConstructionKind>))]
public enum DrawingAnchorConstructionKind { ParallelMidline, LineIntersection, LineExtension }

/// <summary>Finite page geometry, never a pixel-to-mm measurement or a model-derived datum.</summary>
public sealed record DrawingAnchorConstruction
{
    public DrawingAnchorConstructionKind Kind { get; init; }
    public IReadOnlyList<string> SupportAnchorIds { get; init; } = [];
    public required string Evidence { get; init; }
}

/// <summary>Source-side identity, independent of transient SolidWorks face/edge numbers.</summary>
public sealed record DrawingGeometryAnchor
{
    /// <summary>AI-traced source contour or line, independently checked against source foreground pixels.</summary>
    public IReadOnlyList<DrawingPagePoint> SourcePath { get; init; } = [];
    /// <summary>When present, SourcePath is the checked constructed locus; supports retain actual source ink.</summary>
    public DrawingAnchorConstruction? Construction { get; init; }
    public required string Id { get; init; }
    public required string PhysicalEntityId { get; init; }
    public required string FeatureId { get; init; }
    public required string ViewId { get; init; }
    public DrawingAnchorKind Kind { get; init; }
    public required DrawingReviewBox Bounds { get; init; }
    public DrawingReviewState State { get; init; }
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
    public IReadOnlyList<string> CandidateIds { get; init; } = [];
    public required string Evidence { get; init; }
}

/// <summary>Explicit AI correspondence of a circular rim to two axial silhouette lines in another view.
/// The source paths retain their actual two-dimensional kinds; no line is relabeled as a circle.</summary>
public sealed record DrawingCircularProjection
{
    public required string Id { get; init; }
    public required string CircleAnchorId { get; init; }
    public IReadOnlyList<string> SilhouetteAnchorIds { get; init; } = [];
    public required Vector3 AxisDirection { get; init; }
    public DrawingReviewState State { get; init; }
    public required string Evidence { get; init; }
}
