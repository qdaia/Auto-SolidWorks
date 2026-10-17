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

[JsonConverter(typeof(JsonStringEnumConverter<DrawingQuantityKind>))]
public enum DrawingQuantityKind { Linear, Radius, Diameter, Angle, Count, ThreadPitch }
[JsonConverter(typeof(JsonStringEnumConverter<DrawingDerivationKind>))]
public enum DrawingDerivationKind { Arithmetic, Geometry }

/// <summary>AI-reviewed source attachment, not automatic recognition or a correctness certificate.</summary>
public sealed record DrawingDimensionEvidence
{
    public required string FeatureId { get; init; }
    /// <summary>Stable identity of the physical measurement; equal numbers alone do not establish identity.</summary>
    public required string MeasurementId { get; init; }
    public required string ViewId { get; init; }
    public DrawingQuantityKind QuantityKind { get; init; }
    public required DrawingReviewBox TextBounds { get; init; }
    public required DrawingReviewBox TargetBounds { get; init; }
    public required string AttachmentExplanation { get; init; }
    public required string Datum { get; init; }
    public string? StartReference { get; init; }
    public string? EndReference { get; init; }
    public string? StartAnchorId { get; init; }
    public string? EndAnchorId { get; init; }
    public string? TargetAnchorId { get; init; }
}

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
    public DimensionExpression? Expression { get; init; }
    public DrawingDimensionEvidence? BindingEvidence { get; init; }
    /// <summary>Explicit premise graph. Values are never inferred from drawing pixel scale.</summary>
    public IReadOnlyList<string> DerivedFromDimensionIds { get; init; } = [];
    /// <summary>Geometry uses existing source ObservationIds, e.g. a declared symmetry datum, never pixel scale.</summary>
    public DrawingDerivationKind DerivationKind { get; init; }
    /// <summary>Original drawing value when an explicitly authorized deviation is executed.</summary>
    public double? NominalValue { get; init; }
    public string? DeviationReason { get; init; }
    public string? DeviationAuthorization { get; init; }
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
    public DrawingStageScope? StageScope { get; init; }
    public IReadOnlyList<DimensionEquation> DimensionEquations { get; init; } = [];
    public bool RequireGeometryAnchors { get; init; }
    public IReadOnlyList<DrawingGeometryAnchor> GeometryAnchors { get; init; } = [];
    /// <summary>Enabled for newly prepared drawing reviews; legacy contexts remain readable.</summary>
    public bool RequireDimensionEvidence { get; init; }
    /// <summary>Opt-in for legacy callers; mandatory in the current drawing workflow.</summary>
    public bool RequireInterpretationReview { get; init; }
    public IReadOnlyList<SourceInterpretationReview> InterpretationReviews { get; init; } = [];
    /// <summary>Independent source inventory and explicit visual review. Required for complete drawing plans.</summary>
    public DrawingOmissionReview? OmissionReview { get; init; }
    public IReadOnlyList<DrawingFeatureRequirement> Features { get; init; } = [];
    /// <summary>Every operation and declared critical parameter must belong to the source feature inventory.</summary>
    public bool RequireCompleteBindings { get; init; } = true;
    public required string SourcePath { get; init; }
    public DrawingProjection Projection { get; init; } = DrawingProjection.FirstAngle;
    public DrawingFactStatus ProjectionStatus { get; init; } = DrawingFactStatus.Assumed;
    public IReadOnlyList<DrawingPlanView> Views { get; init; } = [];
    public IReadOnlyList<DrawingDimensionFact> Dimensions { get; init; } = [];
}
