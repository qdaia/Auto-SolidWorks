using CadModeling.Ir;
namespace CadModeling.Core;
public sealed record ModelInspectionRequest(string InputPath, IReadOnlyList<EntityQuery>? Queries = null, ModelVerificationSpec? Verification = null,
    IReadOnlyList<GeometryRef>? GeometryReferences = null, IReadOnlyList<MeasurementQuery>? Measurements = null,
    string? DocumentRevision = null, string? SourceRevisionId = null, IReadOnlyList<ConnectivityInspectionQuery>? ConnectivityQueries = null,
    IReadOnlyList<NativeEditabilityProbeSpec>? EditabilityProbes = null);
public sealed record NativeEditabilityProbeSpec(string ProbeId,string DimensionName,double TrialValue,DrawingValueUnit Unit=DrawingValueUnit.Millimeter,string? FeatureName=null);
public sealed record NativeEditabilityProbeResult(string ProbeId,string DimensionName,bool Passed,string Message)
{
    public string? ModelSha256 { get; init; }
    public string? FeatureName { get; init; }
    public DrawingValueUnit Unit { get; init; }
    public double? RequestedTrialValue { get; init; }
    public double? OriginalSystemValue { get; init; }
    public double? TrialSystemValue { get; init; }
    public double? TrialReadbackSystemValue { get; init; }
    public double? RestoreReadbackSystemValue { get; init; }
    public bool TrialRebuildSucceeded { get; init; }
    public bool RestoreSucceeded { get; init; }
    public bool RestoreRebuildSucceeded { get; init; }
    public bool ModelFileUnchanged { get; init; }
}
public sealed record ModelDimension(string Name, double SystemValue, string Kind)
{
    public string ParameterType { get; init; } = "Unknown";
    public string Unit { get; init; } = "Unknown";
    public double? Value { get; init; }
}
public sealed record ModelFeatureInfo(string Name, string Type, bool Suppressed, string? PersistentReference, IReadOnlyList<ModelDimension> Dimensions)
{
    /// <summary>Names obtained from the native feature's direct GetParents dependency list.</summary>
    public IReadOnlyList<string> ParentFeatureNames { get; init; }=[];
    /// <summary>Producer-owned persistent refs of edges selected by supported native fillet/chamfer feature definitions.</summary>
    public IReadOnlyList<string> DrivingEdgePersistentReferences { get; init; }=[];
}
public sealed record ModelEntityInfo(string Kind, string Geometry, string? Name, string? PersistentReference, double? RadiusMm, Vector3? Direction);
public sealed record MeasuredCone(Vector3 AxisStartMm,Vector3 AxisEndMm,Vector3 Direction,double StartRadiusMm,double EndRadiusMm,
    double IncludedAngleDegrees,bool Interior,bool CompleteWall,string? PersistentReference,string? FeatureName)
{
    public string BoundaryEvidence { get; init; }=string.Empty;
}
public sealed record MeasuredCosmeticThread(string FeatureName,string? EdgePersistentReference,Vector3 EntranceCenterMm,Vector3 AxisDirection,
    double DrillRadiusMm,double MajorDiameterMm,string Designation,bool ThroughAll,double BlindDepthMm,bool Complete);
public sealed record ModelInspection(bool Success, string Message, string InputPath,
    GeometrySnapshot? Geometry = null, IReadOnlyList<ModelFeatureInfo>? Features = null,
    IReadOnlyList<ModelEntityInfo>? Entities = null)
{
    public string? ModelSha256 { get; init; }
    public bool ModelReopened { get; init; }
    public bool CaptureComplete { get; init; }
    public bool RebuildSucceeded { get; init; }
    public string? FeatureTreeFingerprint { get; init; }
    public IReadOnlyList<string> Configurations { get; init; } = [];
    public IReadOnlyList<AssemblyComponentResult> Components { get; init; } = [];
    public ModelVerificationResult? Verification { get; init; }
    public IReadOnlyList<MeasuredCylinder> Cylinders { get; init; } = [];
    public IReadOnlyList<MeasuredCone> Cones { get; init; } = [];
    public IReadOnlyList<MeasuredCosmeticThread> CosmeticThreads { get; init; } = [];
    public IReadOnlyList<GeometryRefResolution> GeometryRefResolutions { get; init; } = [];
    public IReadOnlyList<MeasurementResult> Measurements { get; init; } = [];
    public IReadOnlyList<ConnectivityCheck> ConnectivityChecks { get; init; } = [];
    public IReadOnlyList<NativeEditabilityProbeResult> EditabilityProbes { get; init; } = [];
}
