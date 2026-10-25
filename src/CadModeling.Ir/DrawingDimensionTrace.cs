using System.Text.Json.Serialization;
namespace CadModeling.Ir;

public sealed record DrawingPagePoint(double X, double Y);
[JsonConverter(typeof(JsonStringEnumConverter<DrawingMeasurementMode>))]
public enum DrawingMeasurementMode { Unspecified, Horizontal, Vertical, Aligned, Projected, MinorAngle, ReflexAngle, DirectedAngle }
[JsonConverter(typeof(JsonStringEnumConverter<DrawingDimensionTraceKind>))]
public enum DrawingDimensionTraceKind { Leader, LinearDimension, AngularDimension }

/// <summary>All coordinates are in the original page, normalized independently by image width/height.</summary>
public sealed record DrawingDimensionTrace
{
    public DrawingDimensionTraceKind Kind { get; init; }
    public DrawingMeasurementMode MeasurementMode { get; init; }
    /// <summary>Physical measurement direction for linear distances, or oriented angle-plane normal.</summary>
    public Vector3? ModelDirection { get; init; }
    /// <summary>Leader from text to target, or dimension line/arc from first arrow tip to second.</summary>
    public IReadOnlyList<DrawingPagePoint> Path { get; init; } = [];
    /// <summary>For a linear dimension whose visible line extends beyond outside arrows.
    /// Supply both observed arrow tips; Path still retains the actual extended source line.</summary>
    public DrawingPagePoint? StartArrowPoint { get; init; }
    public DrawingPagePoint? EndArrowPoint { get; init; }
    /// <summary>Linear extension lines from physical source contact to corresponding arrow tip; short drafting gaps are allowed by ink coverage.</summary>
    public IReadOnlyList<DrawingPagePoint> StartExtension { get; init; } = [];
    public IReadOnlyList<DrawingPagePoint> EndExtension { get; init; } = [];
    public DrawingPagePoint? AngularVertex { get; init; }
    /// <summary>Required for DirectedAngle; page coordinates have downward-positive Y.</summary>
    public bool? ClockwiseInPage { get; init; }
    public required string Evidence { get; init; }
}
