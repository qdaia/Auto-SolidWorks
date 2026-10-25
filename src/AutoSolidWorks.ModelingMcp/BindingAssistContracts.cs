using CadModeling.Ir;

// Pixel coordinates in these input records refer to the unscaled workspace crop,
// never a screenshot/UI resize and never millimetres. Source contracts stay unchanged.
public sealed record BindingCrop(string Id, int PageNumber, DrawingReviewBox Bounds);
public sealed record BindingFrame(string Id, int PageNumber, int PageWidth, int PageHeight,
    int Left, int Top, int Width, int Height, string ImagePath, string ImageSha256,
    string GridPath, string GridSha256);
public sealed record BindingWorkspace(string Revision, string SourcePath, string SourceSha256,
    string InventoryPath, string InventorySha256, IReadOnlyList<BindingFrame> Frames);
public sealed record BindingAnchorInput
{
    public required string Id { get; init; }
    public required string PhysicalEntityId { get; init; }
    public DrawingAnchorKind Kind { get; init; }
    public required DrawingReviewBox Bounds { get; init; }
    public IReadOnlyList<DrawingPagePoint> SourcePath { get; init; } = [];
    public DrawingAnchorConstruction? Construction { get; init; }
    public DrawingReviewState State { get; init; } = DrawingReviewState.Unresolved;
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
    public IReadOnlyList<string> CandidateIds { get; init; } = [];
    public required string Evidence { get; init; }
}
public sealed record BindingValueInput
{
    public required string Id { get; init; }
    public required string ComponentKey { get; init; }
    public required double Value { get; init; }
    public DrawingValueUnit Unit { get; init; } = DrawingValueUnit.Millimeter;
    public string? OperationId { get; init; }
    public required string ParameterPath { get; init; }
    public string? MeasurementId { get; init; }
    public DrawingQuantityKind QuantityKind { get; init; }
    public required DrawingReviewBox TargetBounds { get; init; }
    public string? StartAnchorId { get; init; }
    public string? EndAnchorId { get; init; }
    public string? TargetAnchorId { get; init; }
    public string? StartReference { get; init; }
    public string? EndReference { get; init; }
    public DrawingDimensionTrace? Trace { get; init; }
    public required string AttachmentExplanation { get; init; }
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
}
public sealed record BindingAnnotationInput
{
    public required string Id { get; init; }
    public required string SourceLiteral { get; init; }
    public required string OperationId { get; init; }
    public required DrawingReviewBox TextBounds { get; init; }
    public DrawingValueUnit LengthUnit { get; init; } = DrawingValueUnit.Millimeter;
    public DrawingReviewState State { get; init; } = DrawingReviewState.Unresolved;
    public required string Evidence { get; init; }
    public DrawingDimensionTrace? LeaderTrace { get; init; }
    public string? LeaderTargetAnchorId { get; init; }
    public IReadOnlyList<BindingValueInput> Dimensions { get; init; } = [];
    public IReadOnlyList<DrawingAnnotationBinding> RetainedComponents { get; init; } = [];
}
public sealed record BindingBatch
{
    public required string FrameId { get; init; }
    public required string ViewId { get; init; }
    public required string FeatureId { get; init; }
    public required string Datum { get; init; }
    public IReadOnlyList<BindingAnchorInput> Anchors { get; init; } = [];
    public IReadOnlyList<BindingAnnotationInput> Annotations { get; init; } = [];
}
