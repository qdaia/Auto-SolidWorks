using System.Text.Json.Serialization;
namespace CadModeling.Ir;

[JsonConverter(typeof(JsonStringEnumConverter<DrawingFactStatus>))]
public enum DrawingFactStatus { Stated, Derived, Assumed, Unknown }
[JsonConverter(typeof(JsonStringEnumConverter<DrawingProjection>))]
public enum DrawingProjection { FirstAngle, ThirdAngle, ReferenceArrow }
[JsonConverter(typeof(JsonStringEnumConverter<ModelDrawingView>))]
public enum ModelDrawingView { Front, Top, Left, Right, Bottom, Rear, Section, Detail, Auxiliary, Isometric }
[JsonConverter(typeof(JsonStringEnumConverter<DrawingValueUnit>))]
public enum DrawingValueUnit { Millimeter, Inch, Meter, Degree, Unitless }

public sealed record DrawingPlanView
{
    public required string Id { get; init; }
    public ModelDrawingView Kind { get; init; }
    public int PageNumber { get; init; } = 1;
    public string? RegionId { get; init; }
    public string? Label { get; init; }
    public DrawingFactStatus Status { get; init; } = DrawingFactStatus.Stated;
}
public sealed record DrawingDimensionFact
{
    public bool Critical { get; init; } = true;
    public required string Id { get; init; }
    public required string OperationId { get; init; }
    /// <summary>Relative snake-case field path in the typed operation; e.g. feature.radius_mm or primitives.0.diameter_mm.</summary>
    public required string ParameterPath { get; init; }
    public required double Value { get; init; }
    public DrawingValueUnit Unit { get; init; } = DrawingValueUnit.Millimeter;
    public DrawingFactStatus Status { get; init; } = DrawingFactStatus.Stated;
    public required string SourceLiteral { get; init; }
    public IReadOnlyList<string> ViewIds { get; init; } = [];
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
    public string? Derivation { get; init; }
}
public sealed record DrawingPlanContext
{
    public IReadOnlyList<DrawingFeatureRequirement> Features { get; init; } = [];
    /// <summary>Every operation and declared critical parameter must belong to the source feature inventory.</summary>
    public bool RequireCompleteBindings { get; init; } = true;
    public required string SourcePath { get; init; }
    public DrawingProjection Projection { get; init; } = DrawingProjection.FirstAngle;
    public DrawingFactStatus ProjectionStatus { get; init; } = DrawingFactStatus.Assumed;
    public IReadOnlyList<DrawingPlanView> Views { get; init; } = [];
    public IReadOnlyList<DrawingDimensionFact> Dimensions { get; init; } = [];
}
