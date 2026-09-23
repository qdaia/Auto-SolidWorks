namespace CadModeling.Core;

public sealed record SectionCaptureRequest
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string NativePath { get; init; }
    public required SectionSpec Spec { get; init; }
    public double EndpointToleranceMm { get; init; } = 0.01;
    public double SheetMarginMm { get; init; } = 10;
}

public sealed record SectionCaptureResult(bool Success, string Message)
{
    public SectionSnapshot? Snapshot { get; init; }
    public bool Complete { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
}
