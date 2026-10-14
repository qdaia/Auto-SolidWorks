namespace CadModeling.Ir;

/// <summary>Source observations are authored before geometry. A contradicted hypothesis cannot be selected.</summary>
public sealed record SourceInterpretationObservation
{
    public required string ViewId { get; init; }
    public required DrawingReviewBox Bounds { get; init; }
    public required string Observation { get; init; }
    public bool Contradicts { get; init; }
}

public sealed record SourceInterpretationHypothesis
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<SourceInterpretationObservation> Observations { get; init; } = [];
}

public sealed record SourceInterpretationReview
{
    public required string FeatureId { get; init; }
    public required string SelectedHypothesisId { get; init; }
    public required string DimensionDatumDerivation { get; init; }
    public IReadOnlyList<SourceInterpretationHypothesis> Hypotheses { get; init; } = [];
}
