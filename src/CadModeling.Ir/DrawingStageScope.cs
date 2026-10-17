namespace CadModeling.Ir;

/// <summary>Explicit incomplete source scope. Never denotes final drawing acceptance.</summary>
public sealed record DrawingStageScope
{
    public required string SessionId { get; init; }
    public required string StageId { get; init; }
    public int Revision { get; init; }
    public required string ProjectionAndUnitsReview { get; init; }
    public required string TopologyAndDatumReview { get; init; }
    public required string DependencyClosureReview { get; init; }
    public IReadOnlyList<string> PendingFeatureIds { get; init; } = [];
}
