namespace CadModeling.Ir;
/// <summary>Trimmed native edge length and explicit endpoints, independent of feature creation parameters.</summary>
public sealed record EdgeShapeCheck
{
    public required string Id { get; init; }
    public required string SourceLiteral { get; init; }
    public IReadOnlyList<string> SourceDimensionIds { get; init; } = [];
    public required EntityQuery Edge { get; init; }
    public double LengthMm { get; init; }
    public required Vector3 StartPointMm { get; init; }
    public required Vector3 EndPointMm { get; init; }
    public double ToleranceMm { get; init; } = .01;
}
/// <summary>G0/G1 and optional G2 inspection at uniform parameter samples on a native seam; not a global certificate.</summary>
public sealed record SurfaceContinuityCheck
{
    public required string Id { get; init; }
    public required string SourceLiteral { get; init; }
    public IReadOnlyList<string> SourceDimensionIds { get; init; } = [];
    public required EntityQuery Edge { get; init; }
    public int Samples { get; init; } = 33;
    public double GapToleranceMm { get; init; } = .01;
    public double AngleToleranceDegrees { get; init; } = .1;
    public bool RequireTangency { get; init; } = true;
    public bool RequireCurvatureContinuity { get; init; }
    /// <summary>Absolute normal curvature difference in inverse millimetres.</summary>
    public double CurvatureTolerancePerMm { get; init; } = 1e-5;
}
