namespace CadModeling.Ir;

/// <summary>One reviewed front-view basis determines all standard orthographic view directions.</summary>
public sealed record DrawingModelOrientation
{
    public Vector3 FrontRight { get; init; } = new(1, 0, 0);
    public Vector3 FrontUp { get; init; } = new(0, 1, 0);
    public DrawingReviewState State { get; init; }
    public required string Evidence { get; init; }
    public IReadOnlyList<DrawingDirectionBinding> DirectionBindings { get; init; } = [];
}

/// <summary>The selected source angle ray points along the positive local sketch X axis.</summary>
public sealed record DrawingDirectionBinding
{
    public required string DimensionId { get; init; }
    public required string OperationId { get; init; }
    public bool UseEndRay { get; init; } = true;
}
