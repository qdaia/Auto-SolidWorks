using System.Text.Json.Serialization;
namespace CadModeling.Ir;

/// <summary>Source visual review of a component not covered by the bounded automatic grammar.</summary>
public sealed record DrawingCalloutReview
{
    public DrawingReviewState State { get; init; }
    public required string SourceLiteral { get; init; }
    public required string SelectedComponent { get; init; }
    public DrawingQuantityKind QuantityKind { get; init; }
    public required double Value { get; init; }
    public DrawingValueUnit Unit { get; init; }
    public required string Evidence { get; init; }
    public IReadOnlyList<string> UnresolvedTokens { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<DrawingCalloutCoverage>))]
public enum DrawingCalloutCoverage { Verified, Partial, Unsupported, Conflict, NotApplicable }
