namespace CadModeling.Core;

public sealed record DrawingExportRequest(string InputPath, string NativePath, string PdfPath, string? TemplatePath = null, DrawingLayoutOptions? Layout = null);
public static class DrawingExportValidation
{
    public static DrawingExportRequest ForAssembly(string nativePath) =>
        new(nativePath,Path.ChangeExtension(nativePath,".SLDDRW"),Path.ChangeExtension(nativePath,".pdf"));

    public static void ValidateOutputs(DrawingExportRequest request)
    {
        DrawingLayoutPlanner.Create(request.Layout);
        foreach(var path in new[]{request.InputPath,request.NativePath,request.PdfPath})
            if(!Path.IsPathFullyQualified(path)) throw new ArgumentException("路径必须是绝对路径。");
        if(!Path.GetExtension(request.NativePath).Equals(".slddrw",StringComparison.OrdinalIgnoreCase)||
           !Path.GetExtension(request.PdfPath).Equals(".pdf",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("输出必须使用 .SLDDRW 和 .PDF 扩展名。");
        foreach(var path in new[]{request.NativePath,request.PdfPath})
            if(File.Exists(path)||Directory.Exists(path)) throw new IOException($"绘制导出不会覆盖现有的输出：{path}");
    }
}
public sealed record ExportedDrawingView(string Name, string Orientation, double Scale, IReadOnlyList<double> OutlineMeters);
public sealed record DrawingViewDimension(string ViewName,ModelDimension Dimension);
public sealed record DrawingExportResult(bool Success, string Message)
{
    public string? RequestId { get; init; }
    public bool OutcomeUnknown { get; init; }
    public string? NativePath { get; init; }
    public string? PdfPath { get; init; }
    public string? SourceSha256 { get; init; }
    public string DrawingOrigin { get; init; } = "generated_from_reference_model";
    public string Projection { get; init; } = "FirstAngle";
    public IReadOnlyList<ExportedDrawingView> Views { get; init; } = [];
    public IReadOnlyList<string> ImportedDimensionNames { get; init; } = [];
    public IReadOnlyList<DrawingViewDimension> ViewDimensions { get; init; } = [];
    public bool DimensionsReopened { get; init; }
    public IReadOnlyList<ModelDimension> SourceDimensions { get; init; } = [];
    public IReadOnlyList<string> UnplacedDimensionNames { get; init; } = [];
    public bool CompleteManufacturingDefinition { get; init; }
    public bool Reopened { get; init; }
    public IReadOnlyList<string> Sheets { get; init; } = [];
    public int ScheduleEntryCount { get; init; }
    public bool ScheduleReopened { get; init; }
}
