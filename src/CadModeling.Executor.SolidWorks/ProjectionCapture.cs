using System.Security.Cryptography;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    public async Task<ProjectionCaptureResult> CaptureProjectionAsync(ProjectionCaptureRequest request, CancellationToken cancellationToken = default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try { return await _sta.InvokeAsync(() => CaptureProjectionOnSta(request), cancellationToken); }
        finally { _serialGate.Release(); }
    }

    private static ProjectionCaptureResult CaptureProjectionOnSta(ProjectionCaptureRequest request)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("native.projection");
        SldWorks? app = null;
        IModelDoc2? source = null;
        IModelDoc2? drawingDocument = null;
        var ownedSource = false;
        string stage = "validation";
        try
        {
            if (!Path.IsPathFullyQualified(request.NativePath) || !File.Exists(request.NativePath) ||
                !Path.GetExtension(request.NativePath).Equals(".sldprt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("捕捉投影需要现有的绝对 .SLDPRT 路径。");
            if (request.SourceDrawingSha256.Length != 64 || !request.SourceDrawingSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("捕捉投影需要独立的源图纸 SHA-256。");
            if (string.IsNullOrWhiteSpace(request.ViewId) || string.IsNullOrWhiteSpace(request.CoordinateFrameId))
                throw new ArgumentException("捕捉投影需要视图和坐标框架ID。");
            if (!double.IsFinite(request.OriginXmm) || !double.IsFinite(request.OriginYmm))
                throw new ArgumentException("投影框架的平移必须是有限的。");

            var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.NativePath)));
            app = (SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application", true)!)!;
            int errors = 0, warnings = 0;
            source = (IModelDoc2?)app.GetOpenDocumentByName(request.NativePath);
            if (source is null)
            {
                source = (IModelDoc2?)PerformanceTrace.Measure("native.open", () => app.OpenDoc6(request.NativePath, (int)swDocumentTypes_e.swDocPART,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings));
                ownedSource = source is not null && Path.GetFullPath(source.GetPathName()).Equals(Path.GetFullPath(request.NativePath), StringComparison.OrdinalIgnoreCase);
            }
            if (source is null || !Path.GetFullPath(source.GetPathName()).Equals(Path.GetFullPath(request.NativePath), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"无法重新打开已保存的精确模型（errors={errors}）。");
            if (source.GetSaveFlag()) throw new InvalidOperationException("捕捉投影拒绝了包含未保存的内存更改的模型。");

            stage = "创建临时绘图视图";
            var template = FindDrawingTemplate(app);
            drawingDocument = (IModelDoc2?)app.NewDocument(template, (int)swDwgPaperSizes_e.swDwgPaperA3size, 0.420, 0.297)
                ?? throw new IOException("无法为投影捕捉创建临时图纸。");
            var drawing = (IDrawingDoc)drawingDocument;
            if (!drawing.SetupSheet6("投影视图", (int)swDwgPaperSizes_e.swDwgPaperA3size, (int)swDwgTemplates_e.swDwgTemplateNone,
                    1, 1, true, "", 0.420, 0.297, "", true, 0, 0, 0, 0, 0, 0))
                throw new IOException("无法初始化临时投影表单。");
            var modelViewNames = (string[])source.GetModelViewNames();
            var requested = OrientationNames(request.Orientation);
            var nativeOrientation = modelViewNames.FirstOrDefault(name => requested.Contains(name, StringComparer.OrdinalIgnoreCase))
                ?? throw new IOException($"请求的模型方向 '{request.Orientation}' 不可用。");
            var view = (IView?)drawing.CreateDrawViewFromModelView3(request.NativePath, nativeOrientation, .210, .1485, 0)
                ?? throw new IOException("无法创建投影视图。");
            view.UseSheetScale = 0;
            view.ScaleDecimal = 1;
            view.SetName2("投影视图");
            // HLV is required here: GetPolylines7 exposes the display-level segmentation and font/style that
            // distinguishes visible from obscured portions. HLR would discard the hidden evidence T09 needs.
            view.SetDisplayMode3(false, (int)swDisplayMode_e.swHIDDEN_GREYED, false, true);
            drawingDocument.ForceRebuild3(false);

            stage = "读回实际的投影边";
            var transform = view.ModelToViewTransform ?? throw new IOException("绘制视图不显示模型到视图的变换。");
            var math = (IMathUtility)app.GetMathUtility();
            var origin = TransformPoint(math, transform, 0, 0, 0);
            // GetPolylines7 returns view-local geometry, while ModelToViewTransform includes
            // the view's sheet placement. Remove only that known placement from the origin;
            // never fit/recenter captured geometry to the expected source.
            var viewPosition = ToDoubles(view.Position, 2, "投影视图平面上的位置");
            origin[0] -= viewPosition[0];
            origin[1] -= viewPosition[1];
            var primitives = new List<ProjectionPrimitive>();
            var limitations = new List<string>();
            var index = 0;
            CaptureDisplayedProjection(view, drawing, origin, request, primitives, limitations, ref index);
            if (sourceHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.NativePath))))
                throw new IOException("原生模型在捕捉投影时被更改。");
            var complete = limitations.Count == 0;
            var message = complete
                ? $"捕获了{primitives.Count}个唯一的原生模型重新打开时可见/不可见的基础元素。"
                : $"捕获了{primitives.Count}支撑可见/不可见的基本体，但投影捕捉不完整（{limitations.Count}限制）。";
            return new(true, message)
            {
                Complete = complete,
                Limitations = limitations,
                Snapshot = new()
                {
                    ViewId = request.ViewId,
                    CoordinateFrameId = request.CoordinateFrameId,
                    SourceSha256 = request.SourceDrawingSha256,
                    NativeModelPath = Path.GetFullPath(request.NativePath),
                    NativeModelSha256 = sourceHash,
                    NativeModelReopened = ownedSource,
                    CaptureComplete = complete,
                    CaptureLimitations = limitations,
                    Primitives = primitives
                }
            };
        }
        catch (Exception exception)
        {
            return new(false, stage + ": " + exception.Message);
        }
        finally
        {
            try { if (drawingDocument is not null && app is not null) PerformanceTrace.Measure("native.close", () => app.CloseDoc(drawingDocument.GetTitle())); } catch (System.Runtime.InteropServices.COMException) { }
            try { if (ownedSource && source is not null && app is not null) PerformanceTrace.Measure("native.close", () => app.CloseDoc(source.GetTitle())); } catch (System.Runtime.InteropServices.COMException) { }
            ReleaseCom(app);
        }
    }

    private static string[] OrientationNames(ProjectionOrientation orientation) => orientation switch
    {
        ProjectionOrientation.Front => ["*Front", "*前视"],
        ProjectionOrientation.Top => ["*Top", "*上视"],
        ProjectionOrientation.Left => ["*Left", "*左视"],
        ProjectionOrientation.Right => ["*Right", "*右视"],
        ProjectionOrientation.Bottom => ["*Bottom", "*下视"],
        ProjectionOrientation.Rear => ["*Back", "*Rear", "*后视"],
        _ => throw new ArgumentOutOfRangeException(nameof(orientation))
    };

    private static double[] TransformPoint(IMathUtility math, MathTransform transform, double x, double y, double z)
    {
        var point = (IMathPoint)math.CreatePoint(new[] { x, y, z });
        var mapped = (IMathPoint)point.MultiplyTransform(transform);
        return (double[])mapped.ArrayData;
    }

    private static ProjectionPointMm Local(double[] point, double[] origin, ProjectionCaptureRequest request) =>
        new((point[0] - origin[0]) * 1000 + request.OriginXmm, (point[1] - origin[1]) * 1000 + request.OriginYmm);

    private static void CaptureDisplayedProjection(IView view, IDrawingDoc drawing, double[] origin, ProjectionCaptureRequest request,
        List<ProjectionPrimitive> primitives, List<string> limitations, ref int index)
    {
        object polylineBuffer;
        object? rawEdges;
        try { rawEdges = view.GetPolylines7(1, out polylineBuffer); }
        catch (Exception ex)
        {
            limitations.Add("SOLIDWORKS 显示多段线捕捉失败：" + ex.Message);
            return;
        }
        if (polylineBuffer is not double[] data || data.Length == 0)
        {
            limitations.Add("SOLIDWORKS 绘图视图返回无显示多段线数据。");
            return;
        }

        try
        {
            var displayRecords = ProjectionDisplayPolylineParser.Parse(data);
            var records = 0;
            foreach (var record in displayRecords)
            {
                var geometry = record.GeometryData;
                var points = new ProjectionPointMm[record.PointCount];
                for (var pointIndex = 0; pointIndex < record.PointCount; pointIndex++)
                {
                    var pointOffset = pointIndex * 3;
                    var raw = new[] { record.PointsXyz[pointOffset], record.PointsXyz[pointOffset + 1], record.PointsXyz[pointOffset + 2] };
                    points[pointIndex] = Local(raw, origin, request);
                }
                records++;
                var style = ResolveProjectionLineStyle(drawing, record.LineStyleToken, record.LineFontToken);
                if (style is null)
                {
                    limitations.Add($"投影 折线 {records} 有未识别的线型/线样式，并且未被接受为可见/隐藏/中心几何。");
                    continue;
                }

                ProjectionPrimitive? primitive = null;
                if (record.Type == 0)
                {
                    if (geometry.Count != 0 || !Collinear(points, 1e-4))
                    {
                        limitations.Add($"投影{style}多段线{records}是非直线的细分曲线（椭圆/样条/自由曲线），超出 T09 范围。");
                        continue;
                    }
                    if (Distance(points[0], points[^1]) <= 1e-9)
                    {
                        limitations.Add($"投影的{style}线{records}收缩到零长度。");
                        continue;
                    }
                    primitive = new()
                    {
                        Id = $"model-line-{++index:D4}", Kind = ProjectionPrimitiveKind.Line,
                        Start = points[0], End = points[^1], LineStyle = style
                    };
                }
                else if (record.Type == 1)
                {
                    if (geometry.Count < 12)
                    {
                        limitations.Add($"投影圆形记录{records}有不完整的中心/起始/结束/法线几何数据。");
                        continue;
                    }
                    var center = Local([geometry[0], geometry[1], geometry[2]], origin, request);
                    var start = Local([geometry[3], geometry[4], geometry[5]], origin, request);
                    var end = Local([geometry[6], geometry[7], geometry[8]], origin, request);
                    var normalLength=Math.Sqrt(geometry[9]*geometry[9]+geometry[10]*geometry[10]+geometry[11]*geometry[11]);
                    if(!double.IsFinite(normalLength)||normalLength<=1e-12)
                    {
                        limitations.Add($"投影的圆形记录{records}无有限法向量。");
                        continue;
                    }
                    var normalZ=Math.Abs(geometry[11])/normalLength;
                    if(normalZ<=1e-8)
                    {
                        try
                        {
                            var segment=EdgeOnCircularProjection.Project(record);
                            var a=segment.StartMeters;var b=segment.EndMeters;
                            AddUnique(primitives,new(){Id=$"model-line-{++index:D4}",Kind=ProjectionPrimitiveKind.Line,
                                Start=Local([a.X,a.Y,a.Z],origin,request),End=Local([b.X,b.Y,b.Z],origin,request),LineStyle=style});
                        }
                        catch(InvalidDataException ex){limitations.Add($"投影正视圆{records}:{ex.Message}");}
                        continue;
                    }
                    if(Math.Abs(normalZ-1)>1e-8)
                    {
                        limitations.Add($"投影圆形记录{records}是一个外椭圆，超出支持的圆/线的范围。");
                        continue;
                    }
                    var radius = Distance(center, start);
                    if (!center.IsFinite || !start.IsFinite || !end.IsFinite || !double.IsFinite(radius) || radius <= 1e-9)
                    {
                        limitations.Add($"投影的圆形记录{records}包含无效的中心/端点/半径。");
                        continue;
                    }
                    if (Distance(start, end) <= 1e-5)
                    {
                        primitive = new()
                        {
                            Id = $"model-circle-{++index:D4}", Kind = ProjectionPrimitiveKind.Circle,
                            Center = center, RadiusMm = radius, LineStyle = style
                        };
                    }
                    else
                    {
                        if (points.Length < 3)
                        {
                            limitations.Add($"投影弧记录{records}缺少一个内部显示点，该点用于确定带有符号的扫描。");
                            continue;
                        }
                        var mid = points[points.Length / 2];
                        var radialResidual = Math.Max(Math.Abs(Distance(center, mid) - radius), Math.Abs(Distance(center, end) - radius));
                        if (radialResidual > 1e-3)
                        {
                            limitations.Add($"投影圆盘记录{records}有{radialResidual:0.######}毫米径向不一致。");
                            continue;
                        }
                        var sweep = SweepThrough(center, start, mid, end);
                        primitive = new()
                        {
                            Id = $"model-arc-{++index:D4}", Kind = ProjectionPrimitiveKind.Arc,
                            Center = center, Start = start, End = end, RadiusMm = radius, SweepDegrees = sweep, LineStyle = style
                        };
                    }
                }
                else
                {
                    limitations.Add($"投影记录{records}有不支持的显示几何类型{record.Type}。");
                    continue;
                }
                AddUnique(primitives, primitive);
            }
            if (rawEdges is Array edges && edges.Length != displayRecords.Count)
                limitations.Add($"轮廓/边线库存计数不同 ({displayRecords.Count}轮廓记录 vs{edges.Length}边槽记录). 轮廓/分割身份不完整.");
        }
        catch (Exception ex)
        {
            limitations.Add("SOLIDWORKS 显示多段线缓冲区无效或不支持：" + ex.Message);
        }
    }

    private static string? ResolveProjectionLineStyle(IDrawingDoc drawing, double lineStyleToken, double lineFontToken)
    {
        try
        {
            return ProjectionLineStyleResolver.Resolve(lineStyleToken, lineFontToken,
                styleToken => drawing.GetLineFontName(styleToken),
                manualFontToken => drawing.GetLineFontName2(manualFontToken));
        }
        catch (Exception) { return null; }
    }

    private static bool Collinear(IReadOnlyList<ProjectionPointMm> points, double toleranceMm)
    {
        var a = points[0]; var b = points[^1]; var length = Distance(a, b);
        if (length <= 1e-12) return false;
        return points.Skip(1).SkipLast(1).All(point =>
            Math.Abs((b.X - a.X) * (a.Y - point.Y) - (a.X - point.X) * (b.Y - a.Y)) / length <= toleranceMm);
    }

    private static double SweepThrough(ProjectionPointMm center, ProjectionPointMm start, ProjectionPointMm mid, ProjectionPointMm end)
    {
        static double Angle(ProjectionPointMm c, ProjectionPointMm p) => Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI;
        static double Ccw(double from, double to) { var delta = (to - from) % 360; return delta < 0 ? delta + 360 : delta; }
        var a = Angle(center, start); var m = Angle(center, mid); var b = Angle(center, end);
        var positive = Ccw(a, b); var sweep = Ccw(a, m) <= positive + 1e-7 ? positive : positive - 360;
        if (!double.IsFinite(sweep) || Math.Abs(sweep) <= 1e-7 || Math.Abs(sweep) >= 360 - 1e-7)
            throw new InvalidOperationException("投影弧扫描是退化了的，或者与完整的圆难以区分。");
        return sweep;
    }

    private static void AddUnique(List<ProjectionPrimitive> primitives, ProjectionPrimitive candidate)
    {
        const double tolerance = 1e-5;
        var duplicate = primitives.Any(existing => existing.LineStyle.Equals(candidate.LineStyle, StringComparison.OrdinalIgnoreCase) &&
            existing.Kind == candidate.Kind && existing.Kind switch
        {
            ProjectionPrimitiveKind.Circle => Distance(existing.Center, candidate.Center) <= tolerance && Math.Abs(existing.RadiusMm - candidate.RadiusMm) <= tolerance,
            ProjectionPrimitiveKind.Line =>
                (Distance(existing.Start, candidate.Start) <= tolerance && Distance(existing.End, candidate.End) <= tolerance) ||
                (Distance(existing.Start, candidate.End) <= tolerance && Distance(existing.End, candidate.Start) <= tolerance),
            ProjectionPrimitiveKind.Arc => Distance(existing.Center, candidate.Center) <= tolerance &&
                Math.Abs(existing.RadiusMm - candidate.RadiusMm) <= tolerance &&
                Distance(existing.Start, candidate.Start) <= tolerance && Distance(existing.End, candidate.End) <= tolerance &&
                Math.Abs(existing.SweepDegrees - candidate.SweepDegrees) <= 1e-5,
            _ => false
        });
        if (!duplicate) primitives.Add(candidate);
    }

    private static double Distance(ProjectionPointMm a, ProjectionPointMm b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

}
