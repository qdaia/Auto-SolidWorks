namespace CadModeling.Core;

public sealed record DrawingExportRequest(string InputPath, string NativePath, string PdfPath, string? TemplatePath = null);
public sealed record ExportedDrawingView(string Name, string Orientation, double Scale, IReadOnlyList<double> OutlineMeters);
public sealed record DrawingExportResult(bool Success, string Message)
{
    public string? NativePath { get; init; }
    public string? PdfPath { get; init; }
    public string? SourceSha256 { get; init; }
    public string DrawingOrigin { get; init; } = "generated_from_reference_model";
    public string Projection { get; init; } = "FirstAngle";
    public IReadOnlyList<ExportedDrawingView> Views { get; init; } = [];
    public IReadOnlyList<string> ImportedDimensionNames { get; init; } = [];
    public IReadOnlyList<ModelDimension> SourceDimensions { get; init; } = [];
    public IReadOnlyList<string> UnplacedDimensionNames { get; init; } = [];
    public bool CompleteManufacturingDefinition { get; init; }
    public bool Reopened { get; init; }
}
