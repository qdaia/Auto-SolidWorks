using CadModeling.Ir;

// A transcript, explicit geometry evidence and explicit consumers. No model geometry
// or check expectation is inferred from the drawing, and no review state is invented.
public sealed record BindingSheet
{
    public IReadOnlyList<BindingSheetGroup> Groups { get; init; } = [];
}
public sealed record BindingSheetGroup
{
    public required string FrameId { get; init; }
    public required string ViewId { get; init; }
    public required string FeatureId { get; init; }
    public required string Datum { get; init; }
    public IReadOnlyList<BindingAnchorInput> Anchors { get; init; } = [];
    public IReadOnlyList<BindingSheetAnnotation> Annotations { get; init; } = [];
}
public sealed record BindingAttachment
{
    public DrawingReviewBox? TargetBounds { get; init; }
    public string? StartAnchorId { get; init; }
    public string? EndAnchorId { get; init; }
    public string? TargetAnchorId { get; init; }
    public string? StartReference { get; init; }
    public string? EndReference { get; init; }
    public DrawingDimensionTrace? Trace { get; init; }
    public string? Explanation { get; init; }
    public IReadOnlyList<string>? ObservationIds { get; init; }
}
public sealed record BindingSheetAnnotation
{
    public required string Id { get; init; }
    public required string SourceLiteral { get; init; }
    public required string OperationId { get; init; }
    public required DrawingReviewBox TextBounds { get; init; }
    public DrawingValueUnit LengthUnit { get; init; } = DrawingValueUnit.Millimeter;
    public DrawingReviewState State { get; init; } = DrawingReviewState.Unresolved;
    public required string Evidence { get; init; }
    public required BindingAttachment Attachment { get; init; }
    public DrawingDimensionTrace? LeaderTrace { get; init; }
    public string? LeaderTargetAnchorId { get; init; }
    public IReadOnlyList<BindingSheetValue> Dimensions { get; init; } = [];
}
public sealed record BindingSheetValue
{
    public required string Id { get; init; }
    public required string ComponentKey { get; init; }
    public required string ParameterPath { get; init; }
    public string? OperationId { get; init; }
    public string? MeasurementId { get; init; }
    public BindingAttachment? Attachment { get; init; }
    public IReadOnlyList<BindingCheckTarget> Checks { get; init; } = [];
}
public sealed record BindingCheckTarget(string CheckId,string ParameterPath);
