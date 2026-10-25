namespace CadModeling.Ir;

/// <summary>A source-derived line segment through a material/void region. Endpoints must be strictly inside the region.</summary>
public sealed record SourceMaterialSegment
{
    public required string Id { get; init; }
    public required Vector3 StartMm { get; init; }
    public required Vector3 EndMm { get; init; }
    public required string SourceExplanation { get; init; }
}

/// <summary>Finite line-interval coverage, not volume/BREP equivalence.</summary>
public sealed record SourceMaterialRegionCheck
{
    public required string Id { get; init; }
    public required string FeatureId { get; init; }
    public required string RegionId { get; init; }
    public required string SourceLiteral { get; init; }
    public string? SourceDecompositionDigest { get; init; }
    public DrawingMaterialState ExpectedState { get; init; }
    public double ToleranceMm { get; init; } = .001;
    public IReadOnlyList<SourceMaterialSegment> Segments { get; init; } = [];
}
