using CadModeling.Ir;
namespace CadModeling.Core;
public sealed record ModelInspectionRequest(string InputPath, IReadOnlyList<EntityQuery>? Queries = null);
public sealed record ModelDimension(string Name, double SystemValue, string Kind);
public sealed record ModelFeatureInfo(string Name, string Type, bool Suppressed, string? PersistentReference, IReadOnlyList<ModelDimension> Dimensions);
public sealed record ModelEntityInfo(string Kind, string Geometry, string? Name, string? PersistentReference, double? RadiusMm, Vector3? Direction);
public sealed record ModelInspection(bool Success, string Message, string InputPath,
    GeometrySnapshot? Geometry = null, IReadOnlyList<ModelFeatureInfo>? Features = null,
    IReadOnlyList<ModelEntityInfo>? Entities = null)
{
    public IReadOnlyList<string> Configurations { get; init; } = [];
    public IReadOnlyList<AssemblyComponentResult> Components { get; init; } = [];
}
