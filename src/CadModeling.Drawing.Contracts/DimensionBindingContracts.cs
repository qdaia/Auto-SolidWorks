using System.Text.Json.Serialization;

namespace CadModeling.Drawing.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<DimensionBindingDecision>))]
public enum DimensionBindingDecision { Candidate, Bound, Conflict, Unresolved, Rejected }

[JsonConverter(typeof(JsonStringEnumConverter<DimensionTargetKind>))]
public enum DimensionTargetKind
{
    Edge,
    Circle,
    Arc,
    Hole,
    Slot,
    Face,
    Feature,
    Depth,
    Pattern,
    Parameter,
    Unknown
}

public sealed record DimensionBindingCandidate
{
    public required string TargetId { get; init; }
    public DimensionTargetKind TargetKind { get; init; } = DimensionTargetKind.Unknown;
    public string? ViewId { get; init; }
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
    public IReadOnlyList<string> FeatureIds { get; init; } = [];
    public bool HasAttachmentEvidence { get; init; }
    public double AttachmentScore { get; init; }
    public string AttachmentBasis { get; init; } = string.Empty;
    public string? OperationId { get; init; }
    public string? ParameterPath { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<DimensionConstraintKind>))]
public enum DimensionConstraintKind
{
    Sum,
    Equality,
    Count,
    DiameterRadius,
    SameAxis,
    EqualSpacing
}

public sealed record DimensionConstraint
{
    public required string ConstraintId { get; init; }
    public DimensionConstraintKind Kind { get; init; }
    public IReadOnlyList<string> FactIds { get; init; } = [];
    public double? ExpectedValue { get; init; }
    public int? ExpectedCount { get; init; }
    public double NumericTolerance { get; init; } = 1e-7;
    public string Basis { get; init; } = string.Empty;
}

public sealed record DimensionConstraintIssue
{
    public required string ConstraintId { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<string> FactIds { get; init; } = [];
}

public sealed record DimensionConstraintReport
{
    public IReadOnlyList<DimensionConstraintIssue> Issues { get; init; } = [];
    public bool Passed => Issues.Count == 0;
}
