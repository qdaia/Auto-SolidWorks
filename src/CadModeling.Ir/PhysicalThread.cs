using System.Text.Json.Serialization;

namespace CadModeling.Ir;

[JsonConverter(typeof(JsonStringEnumConverter<PhysicalThreadLocation>))]
public enum PhysicalThreadLocation { External, Internal }

/// <summary>Single-start nominal metric thread with pinned native library geometry.
/// Internal threads require explicit bore and actual cut envelope; neither route certifies fit tolerances.</summary>
public sealed record PhysicalThreadOptions
{
    public PhysicalThreadLocation Location { get; init; } = PhysicalThreadLocation.External;
    public required string Designation { get; init; }
    public required string ProfilePath { get; init; }
    public required string ProfileSha256 { get; init; }
    public double MajorDiameterMm { get; init; }
    public double PitchMm { get; init; }
    public double LengthMm { get; init; }
    public double RunoutAllowanceMm { get; init; }
    public double? BoreDiameterMm { get; init; }
    public double? MaximumCutDiameterMm { get; init; }
    public bool RightHanded { get; init; } = true;
    public required Vector3 AxisOriginMm { get; init; }
    public required Vector3 AxisIntoPart { get; init; }
}
