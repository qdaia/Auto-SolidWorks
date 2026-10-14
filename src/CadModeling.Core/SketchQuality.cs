using CadModeling.Ir;
namespace CadModeling.Core;
public sealed record FixedSketchEntity(string Role,Vector3? PositionMm);
public sealed record SketchQualityObservation
{
    public bool Complete { get; init; }
    public int ConstraintStatus { get; init; }
    public int DrivingDimensionCount { get; init; }
    public IReadOnlyList<FixedSketchEntity> FixedEntities { get; init; }=[];
    public IReadOnlyList<string> ConsumedBy { get; init; }=[];
    public string? Error { get; init; }
    public bool ReferenceOnly { get; init; }
    public string Status => !Complete?"unverifiable":
        ReferenceOnly&&ConsumedBy.Count>0?"passed":ConstraintStatus==3&&DrivingDimensionCount>0&&ConsumedBy.Count>0&&
        FixedEntities.All(e=>e.Role=="reference_origin")?"passed":"review_required";
}
