using System.Text.Json.Serialization;
namespace CadModeling.Ir;

[JsonConverter(typeof(JsonStringEnumConverter<DrawingAnchorKind>))]
public enum DrawingAnchorKind { Point, Axis, Line, Plane, Circle, Arc, Contour }

/// <summary>Source-side identity, independent of transient SolidWorks face/edge numbers.</summary>
public sealed record DrawingGeometryAnchor
{
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
