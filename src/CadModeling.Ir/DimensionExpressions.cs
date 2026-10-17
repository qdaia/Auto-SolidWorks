using System.Text.Json.Serialization;

namespace CadModeling.Ir;

[JsonConverter(typeof(JsonStringEnumConverter<DimensionExpressionKind>))]
public enum DimensionExpressionKind { Reference, Constant, Add, Subtract, Multiply, Divide, Negate }

/// <summary>Bounded arithmetic only: no executable text, pixel measurements or inferred geometry.</summary>
public sealed record DimensionExpression
{
    public required DimensionExpressionKind Kind { get; init; }
    public string? DimensionId { get; init; }
    public double? Value { get; init; }
    public DrawingValueUnit? Unit { get; init; }
    public IReadOnlyList<DimensionExpression> Arguments { get; init; } = [];
}

/// <summary>An explicitly declared equality, checked independently of the operation's value.</summary>
public sealed record DimensionEquation
{
    public required string Id { get; init; }
    public required DimensionExpression Left { get; init; }
    public required DimensionExpression Right { get; init; }
    public required string SourceExplanation { get; init; }
}
