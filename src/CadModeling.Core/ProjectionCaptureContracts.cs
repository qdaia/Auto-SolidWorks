using System.Text.Json.Serialization;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectionOrientation>))]
public enum ProjectionOrientation { Front, Top, Left, Right, Bottom, Rear }

public sealed record ProjectionCaptureRequest
{
    public required string NativePath { get; init; }
    public required string SourceDrawingSha256 { get; init; }
    public required string ViewId { get; init; }
    public required string CoordinateFrameId { get; init; }
    public ProjectionOrientation Orientation { get; init; } = ProjectionOrientation.Front;
    /// <summary>Fixed translation into the previously calibrated source-view millimeter frame.</summary>
    public double OriginXmm { get; init; }
    public double OriginYmm { get; init; }
}

public sealed record ProjectionCaptureResult(bool Success, string Message)
{
    public ProjectionSnapshot? Snapshot { get; init; }
    public bool Complete { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
}
