using System.Security.Cryptography;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private sealed record CapturedSectionLoop(IReadOnlyList<string> PrimitiveIds, double AreaMm2,
        ProjectionPointMm ProbePoint, Func<ProjectionPointMm, bool> Contains);

    public async Task<SectionCaptureResult> CaptureSectionAsync(SectionCaptureRequest request, CancellationToken cancellationToken = default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try { return await _sta.InvokeAsync(() => CaptureSectionOnSta(request), cancellationToken); }
        finally { _serialGate.Release(); }
    }

    private static SectionCaptureResult CaptureSectionOnSta(SectionCaptureRequest request)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("native.section");
        SldWorks? app = null;
        IModelDoc2? model = null;
        var ownedModel = false;
        var stage = "validation";
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            SectionVerifier.Validate(request.Spec);
            if (request.Spec.Type != SectionType.FullPlane)
                return new(false, "验证：仅支持一个完整的平面剖面。");
            if (!Path.IsPathFullyQualified(request.NativePath) || !File.Exists(request.NativePath) ||
                !Path.GetExtension(request.NativePath).Equals(".sldprt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("截面捕捉需要一个现有的绝对 .SLDPRT 路径。");
            if (!double.IsFinite(request.EndpointToleranceMm) || request.EndpointToleranceMm <= 0 ||
                !double.IsFinite(request.SheetMarginMm) || request.SheetMarginMm <= 0)
                throw new ArgumentException("截面捕捉公差/边缘必须是有限且正数。");

            var modelHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.NativePath)));
            app = (SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application", true)!)!;
            int errors = 0, warnings = 0;
            model = (IModelDoc2?)app.GetOpenDocumentByName(request.NativePath);
            if (model is null)
            {
                model = (IModelDoc2?)PerformanceTrace.Measure("native.open", () => app.OpenDoc6(request.NativePath, (int)swDocumentTypes_e.swDocPART,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings));
                ownedModel = model is not null && Path.GetFullPath(model.GetPathName()).Equals(Path.GetFullPath(request.NativePath), StringComparison.OrdinalIgnoreCase);
            }
            if (model is null || model is not IPartDoc part ||
                !Path.GetFullPath(model.GetPathName()).Equals(Path.GetFullPath(request.NativePath), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"无法重新打开精确保存的原生零件进行剖面捕捉（错误={errors}）。");
            if (model.GetSaveFlag()) throw new InvalidOperationException("截面捕捉拒绝未保存的内存模型状态。");

            stage = "冻结剖面框";
            var normal = ModelVerification.Unit(request.Spec.PlaneNormal);
            var xAxis = ModelVerification.Unit(request.Spec.InPlaneXDirection);
            var yAxis = ModelVerification.Unit(SectionCross(normal, xAxis));
            if (Math.Abs(ModelVerification.Dot(normal, xAxis)) > 1e-6 || ModelVerification.Norm(yAxis) <= 1e-12)
                throw new InvalidOperationException("剖面框架不是一个有效的右手平面基准。");
            var originMm = request.Spec.PlaneOriginMm;
            var bodies = (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? []).OfType<IBody2>().ToArray();
            if (bodies.Length == 0) throw new InvalidOperationException("截面捕捉未找到任何实体体。");
            var (uMin, uMax, vMin, vMax) = SectionBounds(bodies, originMm, xAxis, yAxis, request.SheetMarginMm);
            var modeler = (IModeler)app.GetModeler();
            var primitives = new List<ProjectionPrimitive>();
            var limitations = new List<string>();
            var primitiveIndex = 0;

            stage = "特征实际B-Rep与固定平面相交";
            foreach (var body in bodies)
            {
                IBody2? target = null;
                IBody2? sheet = null;
                ISurface? plane = null;
                try
                {
                    target = (IBody2?)body.Copy2(true) ?? throw new IOException("无法复制固体体，因为非可变截面捕获。");
                    plane = (ISurface?)modeler.CreatePlanarSurface2(
                        new[] { originMm.X / 1000, originMm.Y / 1000, originMm.Z / 1000 },
                        new[] { normal.X, normal.Y, normal.Z }, new[] { xAxis.X, xAxis.Y, xAxis.Z })
                        ?? throw new IOException("无法创建临时剖面平面。");
                    sheet = (IBody2?)modeler.CreateSheetFromSurface(plane, new[] { uMin / 1000, uMax / 1000, vMin / 1000, vMax / 1000 })
                        ?? throw new IOException("无法创建有界临时剖面曲面。");
                    var raw = target.GetIntersectionEdges2(sheet, false) as object[] ?? [];
                    if (raw.Length == 0) continue;
                    if (raw.Length % 2 != 0)
                    {
                        limitations.Add($"体 ' {body.Name} ' 的边交 数据 未配对为目标/工具。");
                        continue;
                    }
                    for (var i = 0; i < raw.Length; i += 2)
                    {
                        if (raw[i] is not IEdge edge)
                        {
                            limitations.Add($"记录{i / 2}体 '{body.Name}' 的交集没有目标体边。");
                            continue;
                        }
                        var primitive = SectionPrimitive(edge, originMm, normal, xAxis, yAxis, ref primitiveIndex, limitations);
                        if (primitive is not null) AddSectionPrimitive(primitives, primitive, request.EndpointToleranceMm);
                    }
                }
                finally
                {
                    ReleaseCom(sheet);
                    ReleaseCom(plane);
                    ReleaseCom(target);
                }
            }
            if (primitives.Count == 0) limitations.Add("固定剖面平面没有产生支撑的线/圆边界 图元。");

            stage = "装配材料和空洞";
            var capturedLoops = BuildSectionLoops(primitives, request.EndpointToleranceMm, limitations);
            var loops = new List<SectionLoop>();
            for (var i = 0; i < capturedLoops.Count; i++)
            {
                var loop = capturedLoops[i];
                var depth = capturedLoops.Where((other, index) => index != i && other.AreaMm2 > loop.AreaMm2 + request.EndpointToleranceMm &&
                    other.Contains(loop.ProbePoint)).Count();
                loops.Add(new()
                {
                    LoopId = $"model-section-loop-{i + 1:D4}",
                    IsVoid = depth % 2 == 1,
                    PrimitiveIds = loop.PrimitiveIds,
                    AreaMm2 = loop.AreaMm2
                });
            }
            if (loops.Count == 0) limitations.Add("无法从实际的交界面边线组装封闭的闭合剖面环。");
            if (modelHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.NativePath))))
                throw new IOException("原生模型在截面被捕捉时被更改。");
            var complete = limitations.Count == 0;
            var snapshot = new SectionSnapshot
            {
                SectionId = request.Spec.SectionId,
                SourceSha256 = request.Spec.SourceSha256,
                ViewId = request.Spec.ViewId,
                CoordinateFrameId = request.Spec.CoordinateFrameId,
                Type = SectionType.FullPlane,
                PlaneOriginMm = originMm,
                PlaneNormal = normal,
                ViewingDirection = ModelVerification.Unit(request.Spec.ViewingDirection),
                InPlaneXDirection = xAxis,
                NativeModelSha256 = modelHash,
                ModelReopened = ownedModel,
                CaptureComplete = complete,
                CaptureLimitations = limitations,
                BoundaryPrimitives = primitives,
                Loops = loops
            };
            return new(true, complete
                ? $"捕获了从重新打开的实际B-Rep中提取的{primitives.Count}边界和{loops.Count}闭合环。"
                : $"剖面 B-Rep 交集已完成，且满足限制条件{limitations.Count}的闭合失败。")
            {
                Complete = complete,
                Limitations = limitations,
                Snapshot = snapshot
            };
        }
        catch (Exception ex)
        {
            return new(false, stage + ": " + ex.Message);
        }
        finally
        {
            try { if (ownedModel && model is not null && app is not null) PerformanceTrace.Measure("native.close", () => app.CloseDoc(model.GetTitle())); } catch (System.Runtime.InteropServices.COMException) { }
            ReleaseCom(app);
        }
    }

    private static ProjectionPrimitive? SectionPrimitive(IEdge edge, Vector3 originMm, Vector3 normal, Vector3 xAxis, Vector3 yAxis,
        ref int index, List<string> limitations)
    {
        var curve = (ICurve?)edge.GetCurve();
        if (curve is null) { limitations.Add("剖面剖切边缘暴露了没有底层曲线。"); return null; }
        ProjectionPointMm Project(IReadOnlyList<double> point) => SectionProject(new(point[0] * 1000, point[1] * 1000, point[2] * 1000), originMm, xAxis, yAxis);
        if (curve.IsLine())
        {
            if ((IVertex?)edge.GetStartVertex() is not { } a || (IVertex?)edge.GetEndVertex() is not { } b ||
                a.GetPoint() is not double[] pa || b.GetPoint() is not double[] pb)
            { limitations.Add("剖面线的边线有不完整的端点。"); return null; }
            var start = Project(pa); var end = Project(pb);
            if (Distance(start, end) <= 1e-9) { limitations.Add("剖面线边在固定剖面框架中塌陷。"); return null; }
            return new() { Id = $"section-line-{++index:D4}", Kind = ProjectionPrimitiveKind.Line, Start = start, End = end, LineStyle = "visible" };
        }
        if (curve.IsCircle())
        {
            var p = ToDoubles(curve.CircleParams, 7, "剖面圆的参数");
            var circleNormal = ModelVerification.Unit(new Vector3(p[3], p[4], p[5]));
            if (Math.Abs(ModelVerification.Dot(circleNormal, normal)) < Math.Cos(.25 * Math.PI / 180))
            { limitations.Add("截面的圆边不与已锁定的截面平面共面。"); return null; }
            var center = Project(p);
            var parameters = (ICurveParamData)edge.GetCurveParams3();
            var startRaw = parameters.StartPoint as double[]; var endRaw = parameters.EndPoint as double[];
            if (startRaw is not { Length: >= 3 } || endRaw is not { Length: >= 3 })
            { limitations.Add("截面的圆形边线有未完成的剪裁端点。"); return null; }
            var start = Project(startRaw); var end = Project(endRaw); var radius = p[6] * 1000;
            if (!double.IsFinite(radius) || radius <= 0) { limitations.Add("截面的圆边有无效的半径。"); return null; }
            if (Distance(start, end) <= 1e-6)
                return new() { Id = $"section-circle-{++index:D4}", Kind = ProjectionPrimitiveKind.Circle, Center = center, RadiusMm = radius, LineStyle = "visible" };
            var delta = parameters.UMaxValue - parameters.UMinValue;
            if (!double.IsFinite(delta) || Math.Abs(delta) <= 1e-12 || Math.Abs(delta) >= Math.PI * 2 - 1e-7)
            { limitations.Add("截面弧的参数范围无效。"); return null; }
            return new()
            {
                Id = $"section-arc-{++index:D4}", Kind = ProjectionPrimitiveKind.Arc, Center = center, Start = start, End = end,
                RadiusMm = radius, SweepDegrees = delta * 180 / Math.PI * (ModelVerification.Dot(circleNormal, normal) >= 0 ? 1 : -1), LineStyle = "visible"
            };
        }
        limitations.Add("剖面交线包含一个超出第一个T10生产范围的样条/椭圆/自由曲线。");
        return null;
    }

    private static IReadOnlyList<CapturedSectionLoop> BuildSectionLoops(IReadOnlyList<ProjectionPrimitive> primitives, double tolerance,
        List<string> limitations)
    {
        var loops = new List<CapturedSectionLoop>();
        foreach (var circle in primitives.Where(item => item.Kind == ProjectionPrimitiveKind.Circle))
        {
            var probe = new ProjectionPointMm(circle.Center.X + circle.RadiusMm * .75, circle.Center.Y);
            loops.Add(new([circle.Id], Math.PI * circle.RadiusMm * circle.RadiusMm, probe,
                point => Distance(point, circle.Center) < circle.RadiusMm - tolerance));
        }
        if (primitives.Any(item => item.Kind == ProjectionPrimitiveKind.Arc))
            limitations.Add("剖面环装配目前不认证包含修剪圆弧的环；圆弧边界被捕捉但剖面不完整。");

        var remaining = primitives.Where(item => item.Kind == ProjectionPrimitiveKind.Line).ToList();
        while (remaining.Count > 0)
        {
            var first = remaining[0]; remaining.RemoveAt(0);
            var ids = new List<string> { first.Id };
            var points = new List<ProjectionPointMm> { first.Start, first.End };
            var failed = false;
            while (Distance(points[^1], points[0]) > tolerance)
            {
                var tail = points[^1];
                var matches = remaining.Select((line, index) => (line, index, ds: Distance(tail, line.Start), de: Distance(tail, line.End)))
                    .Where(item => Math.Min(item.ds, item.de) <= tolerance).ToArray();
                if (matches.Length != 1) { failed = true; break; }
                var next = matches[0]; remaining.RemoveAt(next.index); ids.Add(next.line.Id);
                points.Add(next.ds <= next.de ? next.line.End : next.line.Start);
            }
            if (failed || points.Count < 4)
            {
                limitations.Add("剖面线的交点无法组装成一个唯一的闭合环。");
                continue;
            }
            points.RemoveAt(points.Count - 1);
            var doubleArea = 0d;
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i]; var q = points[(i + 1) % points.Count]; doubleArea += p.X * q.Y - q.X * p.Y;
            }
            if (!double.IsFinite(doubleArea) || Math.Abs(doubleArea) <= 1e-12)
            { limitations.Add("剖面线圈有退化面积。"); continue; }
            var area = Math.Abs(doubleArea) / 2;
            var centroid = SectionPolygonCentroid(points, doubleArea / 2);
            var probe = new ProjectionPointMm(points[0].X * .99 + centroid.X * .01, points[0].Y * .99 + centroid.Y * .01);
            loops.Add(new(ids, area, probe, point => SectionPointInPolygon(point, points)));
        }
        return loops;
    }

    private static (double UMin, double UMax, double VMin, double VMax) SectionBounds(IReadOnlyList<IBody2> bodies,
        Vector3 originMm, Vector3 xAxis, Vector3 yAxis, double marginMm)
    {
        var uMin = double.PositiveInfinity; var uMax = double.NegativeInfinity;
        var vMin = double.PositiveInfinity; var vMax = double.NegativeInfinity;
        foreach (var body in bodies)
        {
            var box = ToDoubles(body.GetBodyBox(), 6, "剖面体盒");
            foreach (var x in new[] { box[0], box[3] }) foreach (var y in new[] { box[1], box[4] }) foreach (var z in new[] { box[2], box[5] })
            {
                var delta = ModelVerification.Sub(new Vector3(x * 1000, y * 1000, z * 1000), originMm);
                var u = ModelVerification.Dot(delta, xAxis); var v = ModelVerification.Dot(delta, yAxis);
                uMin = Math.Min(uMin, u); uMax = Math.Max(uMax, u); vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
            }
        }
        if (!new[] { uMin, uMax, vMin, vMax }.All(double.IsFinite)) throw new InvalidOperationException("剖面体的边界无效。");
        return (uMin - marginMm, uMax + marginMm, vMin - marginMm, vMax + marginMm);
    }

    private static ProjectionPointMm SectionProject(Vector3 pointMm, Vector3 originMm, Vector3 xAxis, Vector3 yAxis)
    {
        var delta = ModelVerification.Sub(pointMm, originMm);
        return new(ModelVerification.Dot(delta, xAxis), ModelVerification.Dot(delta, yAxis));
    }

    private static Vector3 SectionCross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static ProjectionPointMm SectionPolygonCentroid(IReadOnlyList<ProjectionPointMm> points, double signedArea)
    {
        var x = 0d; var y = 0d;
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i]; var q = points[(i + 1) % points.Count]; var cross = p.X * q.Y - q.X * p.Y;
            x += (p.X + q.X) * cross; y += (p.Y + q.Y) * cross;
        }
        return new(x / (6 * signedArea), y / (6 * signedArea));
    }

    private static bool SectionPointInPolygon(ProjectionPointMm point, IReadOnlyList<ProjectionPointMm> polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static void AddSectionPrimitive(List<ProjectionPrimitive> primitives, ProjectionPrimitive candidate, double tolerance)
    {
        bool Same(ProjectionPrimitive a, ProjectionPrimitive b) => a.Kind == b.Kind && a.Kind switch
        {
            ProjectionPrimitiveKind.Line => (Distance(a.Start, b.Start) <= tolerance && Distance(a.End, b.End) <= tolerance) ||
                (Distance(a.Start, b.End) <= tolerance && Distance(a.End, b.Start) <= tolerance),
            ProjectionPrimitiveKind.Circle => Distance(a.Center, b.Center) <= tolerance && Math.Abs(a.RadiusMm - b.RadiusMm) <= tolerance,
            ProjectionPrimitiveKind.Arc => Distance(a.Center, b.Center) <= tolerance && Math.Abs(a.RadiusMm - b.RadiusMm) <= tolerance &&
                ((Distance(a.Start, b.Start) <= tolerance && Distance(a.End, b.End) <= tolerance) ||
                 (Distance(a.Start, b.End) <= tolerance && Distance(a.End, b.Start) <= tolerance)),
            _ => false
        };
        if (!primitives.Any(existing => Same(existing, candidate))) primitives.Add(candidate);
    }
}
