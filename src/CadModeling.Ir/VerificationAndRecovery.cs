using System.Text.Json.Serialization;
namespace CadModeling.Ir;

/// <summary>Requirements authored from the source, separately from operation geometry.</summary>
public sealed record ModelVerificationSpec
{
    public IReadOnlyList<CylinderGroupCheck> CylinderGroups { get; init; } = [];
    public IReadOnlyList<NativeDimensionCheck> NativeDimensions { get; init; } = [];
    public IReadOnlyList<BoundsCheck> Bounds { get; init; } = [];
    public IReadOnlyList<SurfaceSampleCheck> SurfaceSamples { get; init; } = [];
    public IReadOnlyList<BoundaryClearanceCheck> BoundaryClearances { get; init; } = [];
    public IReadOnlyList<VerificationParameterBinding> Bindings { get; init; } = [];
}

public sealed record VerificationParameterBinding(string DimensionId, string CheckId, string ParameterPath);

[JsonConverter(typeof(JsonStringEnumConverter<LocalSurfaceKind>))]
public enum LocalSurfaceKind { Plane, Cylinder, Cone }

/// <summary>Finite source-derived probes on actual trimmed faces, not whole-feature certification.</summary>
public sealed record SurfaceSampleCheck
{
    public required string Id { get; init; }
    public required string SourceLiteral { get; init; }
    public IReadOnlyList<string> SourceDimensionIds { get; init; } = [];
    public LocalSurfaceKind SurfaceKind { get; init; }
    public IReadOnlyList<Vector3> PointsMm { get; init; } = [];
    /// <summary>One outward-from-material normal per point, in model coordinates.</summary>
    public IReadOnlyList<Vector3> OutwardNormals { get; init; } = [];
    public double? DiameterMm { get; init; }
    public double? ConeHalfAngleDegrees { get; init; }
    /// <summary>Computed binding target for a source's included drill-point/countersink angle.</summary>
    public double? ConeIncludedAngleDegrees => ConeHalfAngleDegrees*2;
    /// <summary>Total area of unique faces touched by matching samples; derive independently from source.</summary>
    public double? ExpectedAreaMm2 { get; init; }
    public double AreaToleranceMm2 { get; init; } = 0.1;
    public double ToleranceMm { get; init; } = 0.05;
    public double AngleToleranceDegrees { get; init; } = 0.1;
}

/// <summary>Distance to every solid boundary. Does not classify a point as material or void.</summary>
public sealed record BoundaryClearanceCheck
{
    public required string Id { get; init; }
    public required string SourceLiteral { get; init; }
    public IReadOnlyList<string> SourceDimensionIds { get; init; } = [];
    public IReadOnlyList<Vector3> PointsMm { get; init; } = [];
    public double MinimumDistanceMm { get; init; }
    public double ToleranceMm { get; init; } = 0.01;
}
public sealed record BoundsCheck
{
    public required string Id { get; init; }
    public required string SourceLiteral { get; init; }
    public IReadOnlyList<string> SourceDimensionIds { get; init; } = [];
    public Vector3 SizeMm { get; init; } = new(0,0,0);
    public double ToleranceMm { get; init; } = 0.05;
}

public sealed record CylinderGroupCheck
{
    public required string Id { get; init; }
    public required string SourceLiteral { get; init; }
    public IReadOnlyList<string> SourceDimensionIds { get; init; } = [];
    public double DiameterMm { get; init; }
    /// <summary>One start point on each expected cylinder axis, in global model mm.</summary>
    public IReadOnlyList<Vector3> AxisStartsMm { get; init; } = [];
    public int ExpectedCount => AxisStartsMm.Count;
    public Vector3 Direction { get; init; } = new(0, 0, 1);
    /// <summary>Length of a complete cylindrical wall; excludes drill tips, chamfers and threads.</summary>
    public double LengthMm { get; init; }
    public bool Interior { get; init; } = true;
    /// <summary>All cylindrical walls of this diameter and orientation must be listed when true.</summary>
    public bool ExactCount { get; init; } = true;
    public double ToleranceMm { get; init; } = 0.05;
    public double DirectionToleranceDegrees { get; init; } = 0.1;
}

public sealed record NativeDimensionCheck
{
    public required string Id { get; init; }
    public required string SourceLiteral { get; init; }
    public IReadOnlyList<string> SourceDimensionIds { get; init; } = [];
    public required string DimensionName { get; init; }
    public double Value { get; init; }
    public DrawingValueUnit Unit { get; init; } = DrawingValueUnit.Millimeter;
    public double Tolerance { get; init; } = 0.05;
}

public sealed record ModelingRecoveryOptions
{
    public bool Enabled { get; init; } = true;
    public string? Directory { get; init; }
    public IReadOnlyList<string> AfterOperationIds { get; init; } = [];
    /// <summary>Executor-issued checkpoint manifest. Keep the full corrected operation list.</summary>
    public string? ResumeManifestPath { get; init; }
}

public sealed record CriticalParameterBinding
{
    public required string OperationId { get; init; }
    public required string ParameterPath { get; init; }
    public required string DimensionId { get; init; }
}

public sealed record DrawingFeatureRequirement
{
    public required string Id { get; init; }
    public required string SourceLiteral { get; init; }
    public bool Critical { get; init; } = true;
    public DrawingFactStatus Status { get; init; } = DrawingFactStatus.Stated;
    public string? Derivation { get; init; }
    public IReadOnlyList<string> ViewIds { get; init; } = [];
    public IReadOnlyList<string> OperationIds { get; init; } = [];
    public IReadOnlyList<CriticalParameterBinding> CriticalParameters { get; init; } = [];
    public IReadOnlyList<string> VerificationCheckIds { get; init; } = [];
}
