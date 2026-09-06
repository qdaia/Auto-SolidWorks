using System.Text.Json.Serialization;
namespace CadModeling.Ir;

[JsonConverter(typeof(JsonStringEnumConverter<SketchEntityPart>))]
public enum SketchEntityPart { Segment, StartPoint, EndPoint, CenterPoint }
public sealed record SketchEntityReference
{
    public int PrimitiveIndex { get; init; }
    public int SegmentIndex { get; init; }
    public SketchEntityPart Part { get; init; }
}
[JsonConverter(typeof(JsonStringEnumConverter<SketchConstraintKind>))]
public enum SketchConstraintKind { Coincident, Horizontal, Vertical, Tangent, Concentric, Symmetric, Equal, Parallel, Perpendicular, Fixed, Midpoint }
public sealed record SketchConstraintSpec
{
    public SketchConstraintKind Kind { get; init; }
    public IReadOnlyList<SketchEntityReference> Entities { get; init; } = [];
}
[JsonConverter(typeof(JsonStringEnumConverter<SketchDimensionKind>))]
public enum SketchDimensionKind { Distance, Horizontal, Vertical, Radius, Diameter, Angle }
public sealed record SketchDimensionSpec
{
    public required string Name { get; init; }
    public SketchDimensionKind Kind { get; init; }
    public double Value { get; init; }
    public ProfilePoint LabelPosition { get; init; } = new(10, 10);
    public IReadOnlyList<SketchEntityReference> Entities { get; init; } = [];
}
[JsonConverter(typeof(JsonStringEnumConverter<SketchEditKind>))]
public enum SketchEditKind { Offset, TrimClosest }
public sealed record SketchEditSpec
{
    public SketchEditKind Kind { get; init; }
    public IReadOnlyList<SketchEntityReference> Entities { get; init; } = [];
    public double OffsetMm { get; init; }
    public bool BothDirections { get; init; }
    public bool Chain { get; init; } = true;
    public bool CapEnds { get; init; }
    public ProfilePoint PickPoint { get; init; } = new(0, 0);
}
