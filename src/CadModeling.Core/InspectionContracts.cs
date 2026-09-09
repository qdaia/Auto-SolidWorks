using CadModeling.Ir;
namespace CadModeling.Core;
public sealed record ModelInspectionRequest(string InputPath, IReadOnlyList<EntityQuery>? Queries = null, ModelVerificationSpec? Verification = null);
public sealed record ModelDimension(string Name, double SystemValue, string Kind)
{
    public string ParameterType { get; init; } = "Unknown";
    public string Unit { get; init; } = "Unknown";
    public double? Value { get; init; }
}
public sealed record ModelFeatureInfo(string Name, string Type, bool Suppressed, string? PersistentReference, IReadOnlyList<ModelDimension> Dimensions);
public sealed record ModelEntityInfo(string Kind, string Geometry, string? Name, string? PersistentReference, double? RadiusMm, Vector3? Direction);
public sealed record ModelInspection(bool Success, string Message, string InputPath,
    GeometrySnapshot? Geometry = null, IReadOnlyList<ModelFeatureInfo>? Features = null,
    IReadOnlyList<ModelEntityInfo>? Entities = null)
{
    public IReadOnlyList<string> Configurations { get; init; } = [];
    public IReadOnlyList<AssemblyComponentResult> Components { get; init; } = [];
    public ModelVerificationResult? Verification { get; init; }
    public IReadOnlyList<MeasuredCylinder> Cylinders { get; init; } = [];
}
