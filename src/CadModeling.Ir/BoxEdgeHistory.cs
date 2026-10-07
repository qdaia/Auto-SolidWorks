namespace CadModeling.Ir;

/// <summary>独立的源尺寸定义；不接受调用者提供的成功历史或测量候选。</summary>
public sealed record BoxEdgeHistorySpec
{
    public string SemanticKey { get; init; } = "box.right-top.front-fillet-end";
    public required string WidthDimensionName { get; init; }
    public required string LengthDimensionName { get; init; }
    public required string ThicknessDimensionName { get; init; }
    public required string BaseFeatureName { get; init; }
    public double WidthMm { get; init; }
    public double LengthMm { get; init; }
    public double InitialThicknessMm { get; init; }
    public required string ResizeOperationId { get; init; }
    public required string FilletOperationId { get; init; }
}
