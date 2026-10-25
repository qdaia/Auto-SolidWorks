namespace CadModeling.Ir;

/// <summary>AI transcription census, separate from model parameters. Never OCR or image ground truth.</summary>
public sealed record DrawingAnnotationLedger
{
    public required string SourceSha256 { get; init; }
    public IReadOnlyList<DrawingSourceAnnotation> Annotations { get; init; } = [];
    public IReadOnlyList<DrawingAnnotationSweep> Sweeps { get; init; } = [];
}
public sealed record DrawingSourceAnnotation
{
    public required string Id { get; init; }
    public required string ViewId { get; init; }
    public required DrawingReviewBox TextBounds { get; init; }
    public required string SourceLiteral { get; init; }
    public DrawingValueUnit LengthUnit { get; init; } = DrawingValueUnit.Millimeter;
    public required string FeatureId { get; init; }
    public required string OperationId { get; init; }
    public DrawingDimensionTrace? LeaderTrace { get; init; }
    public string? LeaderTargetAnchorId { get; init; }
    public bool Deferred { get; init; }
    public DrawingReviewState State { get; init; }
    public string Evidence { get; init; } = "";
    public IReadOnlyList<DrawingAnnotationBinding> Components { get; init; } = [];
}
public sealed record DrawingAnnotationBinding
{
    public required string ComponentKey { get; init; }
    public string? DimensionId { get; init; }
    /// <summary>Non-numeric manufacturing tokens must be explicitly retained and reviewed; not certified.</summary>
    public string? RetainedLiteral { get; init; }
    public string Evidence { get; init; } = "";
}
public sealed record DrawingAnnotationSweep
{
    public required string RegionId { get; init; }
    /// <summary>1: source-first census; 2: reverse sweep after bindings.</summary>
    public int Pass { get; init; }
    public DrawingReviewState State { get; init; }
    public IReadOnlyList<string> AnnotationIds { get; init; } = [];
    public string Evidence { get; init; } = "";
}
